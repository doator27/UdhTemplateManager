using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Merges a legacy/standalone SQLite copy of the database (from a previously-separate
/// instance of the application) into the master database. Because several instances ran
/// in parallel before centralization, the same Id in the legacy file may belong to an
/// entirely different real-world record than the one with that Id in the master database.
/// <para>
/// For every table (processed in foreign-key dependency order) each source row is compared
/// against any master row that already has the same Id, using a table-specific set of
/// "identity" fields (ignoring volatile/audit columns). A match means the row already exists
/// and is skipped. A free Id is inserted unchanged. A conflicting Id is inserted with a newly
/// allocated Id, and every foreign key elsewhere that pointed at the old Id is remapped to the
/// new one before it is written.
/// </para>
/// <para>
/// Call <see cref="PreviewAsync"/> first to compute a dry-run <see cref="MergeImportPlan"/>
/// safe to show the user, then pass it to <see cref="ApplyAsync"/> only after explicit
/// confirmation to actually write to the master database.
/// </para>
/// </summary>
public sealed class DatabaseMergeImportService
{
    private readonly Func<AppDbContext> _targetContextFactory;

    // Captured during PreviewAsync, consumed by ApplyAsync. Each step receives the live
    // target context used for the apply transaction and performs the inserts for one table.
    private List<Func<AppDbContext, Task>>? _pendingApplySteps;
    private string? _previewedSourcePath;

    /// <summary>Initializes a new instance of <see cref="DatabaseMergeImportService"/>.</summary>
    /// <param name="targetContextFactory">Creates a new <see cref="AppDbContext"/> pointed at the master database (e.g. <c>DatabaseInitializer.CreateContext</c>).</param>
    public DatabaseMergeImportService(Func<AppDbContext> targetContextFactory)
    {
        _targetContextFactory = targetContextFactory;
    }

    /// <summary>
    /// Reads <paramref name="sqliteFilePath"/> and the master database and computes a dry-run
    /// plan describing what would be imported. Does not write anything.
    /// </summary>
    public async Task<MergeImportPlan> PreviewAsync(string sqliteFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(sqliteFilePath))
        {
            throw new FileNotFoundException("Legacy SQLite database file not found.", sqliteFilePath);
        }

        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteFilePath};Mode=ReadOnly")
            .Options;

        await using var source = new AppDbContext(sourceOptions);
        await using var target = _targetContextFactory();

        var plan = new MergeImportPlan { SourceFilePath = sqliteFilePath };
        var steps = new List<Func<AppDbContext, Task>>();
        var remap = new IdRemapTracker();

        await MergeTableAsync<UserProfile>(
            source, target, remap, plan, steps, "UserProfiles",
            e => $"{e.UserName}|{e.DefaultTemplateSaveLocation}",
            describe: e => e.UserName,
            remapForeignKeys: null,
            ct);

        await MergeTableAsync<Customer>(
            source, target, remap, plan, steps, "Customers",
            e => e.CustomerName,
            describe: e => e.CustomerName,
            remapForeignKeys: null,
            ct);

        await MergeTableAsync<Manufacturer>(
            source, target, remap, plan, steps, "Manufacturers",
            e => e.ManufacturerName,
            describe: e => e.ManufacturerName,
            remapForeignKeys: null,
            ct);

        await MergeDescriptionsAsync(source, target, remap, plan, steps, ct);

        await MergeTableAsync<DoorMaterial>(
            source, target, remap, plan, steps, "DoorMaterials",
            e => e.Material,
            describe: e => e.Material,
            remapForeignKeys: null,
            ct);

        await MergeTableAsync<ProjectManager>(
            source, target, remap, plan, steps, "ProjectManagers",
            e => e.ProjectManagerName,
            describe: e => e.ProjectManagerName,
            remapForeignKeys: null,
            ct);

        await MergeTableAsync<HardwareItem>(
            source, target, remap, plan, steps, "HardwareItems",
            e => $"{e.ManufacturerId}|{e.DescriptionId}|{e.ModelNumber}",
            describe: e => e.ModelNumber,
            remapForeignKeys: e =>
            {
                e.ManufacturerId = remap.Resolve("Manufacturers", e.ManufacturerId);
                e.DescriptionId = remap.Resolve("Descriptions", e.DescriptionId);
            },
            ct);

        await MergeTableAsync<IndividualTemplate>(
            source, target, remap, plan, steps, "IndividualTemplates",
            e => $"{e.ManufacturerId}|{e.DescriptionId}|{e.TemplateNumber}",
            describe: e => e.TemplateNumber,
            remapForeignKeys: e =>
            {
                e.ManufacturerId = remap.Resolve("Manufacturers", e.ManufacturerId);
                e.DescriptionId = remap.Resolve("Descriptions", e.DescriptionId);
            },
            ct);

        await MergeTableAsync<HardwareItemTemplate>(
            source, target, remap, plan, steps, "HardwareItemTemplates",
            e => $"{e.HardwareItemId}|{e.IndividualTemplateId}|{e.JobId}",
            describe: e => $"HW {e.HardwareItemId} <-> Tpl {e.IndividualTemplateId}",
            remapForeignKeys: e =>
            {
                e.HardwareItemId = remap.Resolve("HardwareItems", e.HardwareItemId);
                e.IndividualTemplateId = remap.Resolve("IndividualTemplates", e.IndividualTemplateId);
                e.JobId = remap.ResolveNullable("Jobs", e.JobId);
            },
            ct);

        await MergeTableAsync<Job>(
            source, target, remap, plan, steps, "Jobs",
            // Compares every field except datetime-based ones (CreatedAt, NotesUpdatedAt,
            // MissingTemplateNotifiedAt) and LastActiveReleaseId, which is deliberately
            // deferred/nulled above and resolved later once JobReleases are merged.
            e => $"{e.JobNumber}|{e.JobName}|{e.CustomerId}|{e.ProjectManagerId}|{e.UserProfileId}|{e.IsComplete}|{e.Notes}",
            describe: e => $"{e.JobNumber} - {e.JobName}",
            remapForeignKeys: e =>
            {
                e.CustomerId = remap.Resolve("Customers", e.CustomerId);
                e.ProjectManagerId = remap.Resolve("ProjectManagers", e.ProjectManagerId);
                e.UserProfileId = remap.Resolve("UserProfiles", e.UserProfileId);
                e.LastActiveReleaseId = null; // resolved after JobReleases are merged below (self-referential ordering)
            },
            ct);

        await MergeTableAsync<JobRelease>(
            source, target, remap, plan, steps, "JobReleases",
            e => $"{e.JobId}|{e.ReleaseNumber}|{e.ReleaseLabel}",
            describe: e => e.ReleaseLabel,
            remapForeignKeys: e => e.JobId = remap.Resolve("Jobs", e.JobId),
            ct);

        await MergeTableAsync<JobHardware>(
            source, target, remap, plan, steps, "JobHardware",
            e => $"{e.JobId}|{e.HardwareItemId}|{e.ReleaseId}|{e.CustomDescription}",
            describe: e => $"Job {e.JobId} - HW {e.HardwareItemId}",
            remapForeignKeys: e =>
            {
                e.JobId = remap.Resolve("Jobs", e.JobId);
                e.HardwareItemId = remap.Resolve("HardwareItems", e.HardwareItemId);
                e.ReleaseId = remap.ResolveNullable("JobReleases", e.ReleaseId);
            },
            ct);

        await MergeTableAsync<JobTemplateSnapshot>(
            source, target, remap, plan, steps, "JobTemplateSnapshots",
            e => $"{e.JobId}|{e.IndividualTemplateId}|{e.SnapshotLocalLink}",
            describe: e => e.SnapshotLocalLink,
            remapForeignKeys: e =>
            {
                e.JobId = remap.Resolve("Jobs", e.JobId);
                e.IndividualTemplateId = remap.Resolve("IndividualTemplates", e.IndividualTemplateId);
            },
            ct);

        await MergeTableAsync<JobAttachment>(
            source, target, remap, plan, steps, "JobAttachments",
            e => $"{e.JobId}|{e.FileName}|{e.StoredPath}",
            describe: e => e.FileName,
            remapForeignKeys: e => e.JobId = remap.Resolve("Jobs", e.JobId),
            ct);

        await MergeBulkAddDraftsAsync(source, target, remap, plan, steps, ct);

        await MergeTableAsync<IgnoredTemplateDuplicate>(
            source, target, remap, plan, steps, "IgnoredTemplateDuplicates",
            e => $"{e.SharedLink}|{e.LinkType}",
            describe: e => e.SharedLink,
            remapForeignKeys: null,
            ct);

        _pendingApplySteps = steps;
        _previewedSourcePath = sqliteFilePath;
        return plan;
    }

    /// <summary>
    /// Writes a plan previously produced by <see cref="PreviewAsync"/> to the master database.
    /// Must be called with the exact <see cref="MergeImportPlan"/> instance returned by the
    /// most recent <see cref="PreviewAsync"/> call on this service instance. All inserts happen
    /// in a single transaction — if anything fails, nothing is written.
    /// </summary>
    public async Task ApplyAsync(MergeImportPlan plan, CancellationToken ct = default)
    {
        if (_pendingApplySteps is null || _previewedSourcePath != plan.SourceFilePath)
        {
            throw new InvalidOperationException(
                "ApplyAsync must be called with the plan returned by the most recent PreviewAsync call.");
        }

        await using var target = _targetContextFactory();
        var strategy = target.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                target.ChangeTracker.Clear();
                await using var transaction = await target.Database.BeginTransactionAsync(ct);
                try
                {
                    foreach (var step in _pendingApplySteps)
                    {
                        await step(target);
                    }

                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            });
        }
        finally
        {
            // The plan has been consumed (successfully or not) — require a fresh preview before
            // allowing another apply, since the master database state may have changed.
            _pendingApplySteps = null;
            _previewedSourcePath = null;
        }
    }

    // ── Generic per-table merge routine ─────────────────────────────────────────────

    private static async Task MergeTableAsync<TEntity>(
        AppDbContext source,
        AppDbContext target,
        IdRemapTracker remap,
        MergeImportPlan plan,
        List<Func<AppDbContext, Task>> applySteps,
        string tableName,
        Func<TEntity, string> identityKey,
        Func<TEntity, string> describe,
        Action<TEntity>? remapForeignKeys,
        CancellationToken ct)
        where TEntity : class
    {
        var (getId, setId) = EntityIdAccessors.For<TEntity>();

        var preview = new TableMergePreview { TableName = tableName };
        plan.TablePreviews.Add(preview);

        if (!await TargetTableExistsAsync(target, tableName, ct))
        {
            preview.Note = "table does not exist in the master database yet (pending migrations?) — run the app once to update the schema, then retry.";
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        List<TEntity> sourceRows;
        try
        {
            sourceRows = await source.Set<TEntity>().AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            preview.Note = $"could not read from the legacy source file (older schema?): {ex.Message}";
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        if (sourceRows.Count == 0)
        {
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        var targetRows = await target.Set<TEntity>().AsNoTracking().ToDictionaryAsync(getId, ct);
        var nextId = (targetRows.Count == 0 ? 0 : targetRows.Keys.Max()) + 1;
        var toInsert = new List<TEntity>();

        // Index existing master rows by their identity fields (not just Id) so a row that
        // already exists under a *different* Id than it had in the legacy file is still
        // recognized as a duplicate instead of being inserted again.
        var targetByKey = new Dictionary<string, int>();
        foreach (var kvp in targetRows)
        {
            targetByKey[identityKey(kvp.Value)] = kvp.Key;
        }

        foreach (var row in sourceRows)
        {
            var oldId = getId(row);
            remapForeignKeys?.Invoke(row);
            var key = identityKey(row);

            if (targetByKey.TryGetValue(key, out var matchedId))
            {
                remap.Record(tableName, oldId, matchedId);
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = matchedId, Action = TableMergeAction.Skip, Description = describe(row) });
                continue;
            }

            if (targetRows.ContainsKey(oldId))
            {
                var newId = nextId++;
                setId(row, newId);
                remap.Record(tableName, oldId, newId);
                toInsert.Add(row);
                targetByKey[key] = newId;
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = newId, Action = TableMergeAction.InsertNewId, Description = describe(row) });
            }
            else
            {
                remap.Record(tableName, oldId, oldId);
                toInsert.Add(row);
                targetByKey[key] = oldId;
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = oldId, Action = TableMergeAction.InsertSameId, Description = describe(row) });
            }
        }

        applySteps.Add(async ctx =>
        {
            if (toInsert.Count == 0) return;
            await ctx.Set<TEntity>().AddRangeAsync(toInsert);
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();
        });
    }

    // ── Descriptions: self-referential (ParentId), needs a two-pass remap ──────────

    private static async Task MergeDescriptionsAsync(
        AppDbContext source,
        AppDbContext target,
        IdRemapTracker remap,
        MergeImportPlan plan,
        List<Func<AppDbContext, Task>> applySteps,
        CancellationToken ct)
    {
        const string tableName = "Descriptions";
        var preview = new TableMergePreview { TableName = tableName };
        plan.TablePreviews.Add(preview);

        if (!await TargetTableExistsAsync(target, tableName, ct))
        {
            preview.Note = "table does not exist in the master database yet (pending migrations?) — run the app once to update the schema, then retry.";
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        List<Description> sourceRows;
        try
        {
            sourceRows = await source.Set<Description>().AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            preview.Note = $"could not read from the legacy source file (older schema?): {ex.Message}";
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        if (sourceRows.Count == 0)
        {
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        var targetRows = await target.Set<Description>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var nextId = (targetRows.Count == 0 ? 0 : targetRows.Keys.Max()) + 1;
        var toInsert = new List<Description>();

        static string Key(Description d) => $"{d.DescriptionText}|{d.SortOrder}";

        // Index existing master rows by content so a row already present under a different Id
        // (e.g. imported previously, or diverged from the legacy file's numbering) is matched
        // instead of being inserted again as a duplicate.
        var targetByKey = new Dictionary<string, int>();
        foreach (var kvp in targetRows)
        {
            targetByKey[Key(kvp.Value)] = kvp.Key;
        }

        // Pass 1: decide Skip / InsertSameId / InsertNewId using DescriptionText + SortOrder only
        // (ParentId is deferred because a parent row's new Id may not be known yet).
        foreach (var row in sourceRows)
        {
            var oldId = row.Id;
            var key = Key(row);

            if (targetByKey.TryGetValue(key, out var matchedId))
            {
                remap.Record(tableName, oldId, matchedId);
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = matchedId, Action = TableMergeAction.Skip, Description = row.DescriptionText });
                continue;
            }

            if (targetRows.ContainsKey(oldId))
            {
                var newId = nextId++;
                row.Id = newId;
                remap.Record(tableName, oldId, newId);
                toInsert.Add(row);
                targetByKey[key] = newId;
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = newId, Action = TableMergeAction.InsertNewId, Description = row.DescriptionText });
            }
            else
            {
                remap.Record(tableName, oldId, oldId);
                toInsert.Add(row);
                targetByKey[key] = oldId;
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = oldId, Action = TableMergeAction.InsertSameId, Description = row.DescriptionText });
            }
        }

        // Pass 2: now every old Id in this batch maps to a final Id — remap ParentId.
        foreach (var row in toInsert)
        {
            row.ParentId = remap.ResolveNullable(tableName, row.ParentId);
        }

        applySteps.Add(async ctx =>
        {
            if (toInsert.Count == 0) return;
            await ctx.Set<Description>().AddRangeAsync(toInsert);
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();
        });
    }

    // ── BulkAddDrafts: SavedAt may be malformed in old files; skip unparseable rows ─

    private static async Task MergeBulkAddDraftsAsync(
        AppDbContext source,
        AppDbContext target,
        IdRemapTracker remap,
        MergeImportPlan plan,
        List<Func<AppDbContext, Task>> applySteps,
        CancellationToken ct)
    {
        const string tableName = "BulkAddDrafts";
        var preview = new TableMergePreview { TableName = tableName };
        plan.TablePreviews.Add(preview);

        if (!await TargetTableExistsAsync(target, tableName, ct))
        {
            preview.Note = "table does not exist in the master database yet (pending migrations?) — run the app once to update the schema, then retry.";
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        List<BulkAddDraft> sourceRows;
        try
        {
            sourceRows = await source.Set<BulkAddDraft>().AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            preview.Note = $"could not read from the legacy source file (older schema?): {ex.Message}";
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        if (sourceRows.Count == 0)
        {
            applySteps.Add(_ => Task.CompletedTask);
            return;
        }

        var targetRows = await target.Set<BulkAddDraft>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var nextId = (targetRows.Count == 0 ? 0 : targetRows.Keys.Max()) + 1;
        var toInsert = new List<BulkAddDraft>();

        static string Key(BulkAddDraft d) => $"{d.JobId}|{d.DraftJson}";

        var targetByKey = new Dictionary<string, int>();
        foreach (var kvp in targetRows)
        {
            targetByKey[Key(kvp.Value)] = kvp.Key;
        }

        foreach (var row in sourceRows)
        {
            var oldId = row.Id;
            row.JobId = remap.Resolve("Jobs", row.JobId);
            var key = Key(row);

            if (targetByKey.TryGetValue(key, out var matchedId))
            {
                remap.Record(tableName, oldId, matchedId);
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = matchedId, Action = TableMergeAction.Skip, Description = $"Draft for Job {row.JobId}" });
                continue;
            }

            if (targetRows.ContainsKey(oldId))
            {
                var newId = nextId++;
                row.Id = newId;
                remap.Record(tableName, oldId, newId);
                toInsert.Add(row);
                targetByKey[key] = newId;
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = newId, Action = TableMergeAction.InsertNewId, Description = $"Draft for Job {row.JobId}" });
            }
            else
            {
                remap.Record(tableName, oldId, oldId);
                toInsert.Add(row);
                targetByKey[key] = oldId;
                preview.Results.Add(new MergeRowResult { OldId = oldId, NewId = oldId, Action = TableMergeAction.InsertSameId, Description = $"Draft for Job {row.JobId}" });
            }
        }

        applySteps.Add(async ctx =>
        {
            if (toInsert.Count == 0) return;
            await ctx.Set<BulkAddDraft>().AddRangeAsync(toInsert);
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();
        });
    }

    /// <summary>
    /// Checks whether <paramref name="tableName"/> exists in the dbo schema of the target
    /// database's connection. Guards against master databases whose schema was created
    /// out-of-band and may be missing tables added by later migrations — without this check
    /// a missing table surfaces as an unhandled "Invalid object name" SQL error mid-preview.
    /// </summary>
    private static async Task<bool> TargetTableExistsAsync(AppDbContext target, string tableName, CancellationToken ct)
    {
        var connection = target.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN OBJECT_ID(@name) IS NOT NULL THEN 1 ELSE 0 END";

            var param = command.CreateParameter();
            param.ParameterName = "@name";
            param.Value = $"[dbo].[{tableName}]";
            command.Parameters.Add(param);

            var result = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result) == 1;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Caches reflection-based get/set delegates for each entity type's <c>Id</c> property so
    /// the generic merge routine can work across the many unrelated entity classes without
    /// requiring them to implement a shared interface.
    /// </summary>
    private static class EntityIdAccessors
    {
        private static readonly Dictionary<Type, (Func<object, int> Get, Action<object, int> Set)> Cache = new();

        public static (Func<TEntity, int> Get, Action<TEntity, int> Set) For<TEntity>() where TEntity : class
        {
            var type = typeof(TEntity);
            if (!Cache.TryGetValue(type, out var accessors))
            {
                var prop = type.GetProperty("Id") ?? throw new InvalidOperationException($"Type {type.Name} has no Id property.");
                accessors = (
                    e => (int)prop.GetValue(e)!,
                    (e, v) => prop.SetValue(e, v));
                Cache[type] = accessors;
            }

            return (e => accessors.Get(e), (e, v) => accessors.Set(e, v));
        }
    }

    /// <summary>Tracks old-Id → new-Id mappings per table, computed during a single preview pass.</summary>
    private sealed class IdRemapTracker
    {
        private readonly Dictionary<string, Dictionary<int, int>> _maps = new();

        public void Record(string table, int oldId, int newId)
        {
            if (!_maps.TryGetValue(table, out var map))
            {
                map = new Dictionary<int, int>();
                _maps[table] = map;
            }

            map[oldId] = newId;
        }

        /// <summary>Resolves an old Id to its final (master-database) Id for the given table.</summary>
        public int Resolve(string table, int oldId)
        {
            if (_maps.TryGetValue(table, out var map) && map.TryGetValue(oldId, out var newId))
            {
                return newId;
            }

            // Table not yet processed, or Id not seen — leave unchanged as a safe fallback.
            return oldId;
        }

        /// <summary>Nullable-FK overload of <see cref="Resolve"/>.</summary>
        public int? ResolveNullable(string table, int? oldId) => oldId.HasValue ? Resolve(table, oldId.Value) : null;
    }
}
