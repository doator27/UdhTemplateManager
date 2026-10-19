using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.App.Navigation;
using HardwareTemplateBuilder.App.Views;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App;

/// <summary>
/// The application's main window. Hosts the persistent menu bar and the navigation content area.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The view router managing content area navigation.</summary>
    private readonly ViewRouter _router;

    /// <summary>Initializes the main window, registers all views, and shows the dashboard.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _router = new ViewRouter(ContentArea);
        RegisterViews();
        WireMenuItems();

        // Do NOT navigate here: the database connection may not be configured yet on first
        // launch, and views like JobsView query the database as soon as their Loaded event
        // fires — which happens when the window's visual tree is attached, before App.axaml.cs's
        // Opened handler gets a chance to show the DatabaseSetupDialog. Navigation is deferred
        // to App.axaml.cs's NavigateToJobsIfEmpty() call, made only after the database
        // connection has been resolved and initialized.

        // Phase 24: Run missing-template alert check at startup (best-effort, background).
        Task.Run(() =>
        {
            try
            {
                new MissingTemplateAlertService(DatabaseInitializer.CreateContext).RunCheck();
            }
            catch { /* Swallow: startup alert failure must not crash the app. */ }
        });

    }

    /// <summary>Registers all application views with the router.</summary>
    private void RegisterViews()
    {
        _router.Register("Dashboard", () =>
        {
            var dashboard = new DashboardView();
            dashboard.NavigationRequested += NavigateTo;
            return dashboard;
        });
        _router.Register("TemplateLookup", () => { var v = new TemplateLookupView();   v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Jobs", () =>
        {
            var jobsView = new JobsView();
            jobsView.NavigationRequested += NavigateTo;
            return jobsView;
        });
        _router.Register("HardwareItems",    () => { var v = new HardwareItemsView();    v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Templates",        () => { var v = new IndividualTemplatesView(); v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Customers",        () => { var v = new CustomersView();        v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Manufacturers",    () => { var v = new ManufacturersView();    v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Descriptions",     () => { var v = new DescriptionsView();     v.NavigationRequested += NavigateTo; return v; });
        _router.Register("ProjectManagers",  () => { var v = new ProjectManagersView();  v.NavigationRequested += NavigateTo; return v; });
        _router.Register("UserProfiles",     () => { var v = new UserProfilesView();     v.NavigationRequested += NavigateTo; return v; });
        _router.Register("AppSettings",      () => { var v = new AppSettingsView();      v.NavigationRequested += NavigateTo; return v; });
        _router.Register("BatchGenerate",        () => { var v = new BatchGenerateView();        v.NavigationRequested += NavigateTo; return v; });
        _router.Register("RefreshTemplates",     () => { var v = new RefreshTemplatesView();     v.NavigationRequested += NavigateTo; return v; });
        _router.Register("DeduplicateHardware", () => { var v = new DeduplicateHardwareView(); v.NavigationRequested += NavigateTo; return v; });
        _router.Register("JobImportWizard", () => { var v = new JobImportWizardView(); v.NavigationRequested += NavigateTo; return v; });
    }

    /// <summary>Wires all menu item click events to their corresponding navigation targets.</summary>
    private void WireMenuItems()
    {
        MenuFileAbout.Click += async (_, _) =>
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            var verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            await DialogHelper.ShowInfoAsync(this,
                $"Hardware Template Builder  v{verStr}\n\n" +
                "Manages door hardware templates and generates PDF packages for job orders.\n\n" +
                "Built with .NET 8 · Avalonia UI · PDFsharp · QuestPDF · SQL Server",
                "About Hardware Template Builder");
        };
        MenuFileChangeUser.Click += async (_, _) =>
        {
            var machineId = MachineIdentityService.GetMachineId();
            // Temporarily clear active profile so the picker cannot be dismissed without re-selecting.
            SessionService.ActiveUserProfile = null;
            var picker = new ProfilePickerDialog(machineId);
            await picker.ShowDialog(this);
            SetActiveUser(SessionService.ActiveUserProfile?.UserName ?? "Unknown");
        };
        MenuFileDatabaseLocation.Click += async (_, _) =>
        {
            // Not required — user can cancel without changing the location.
            var dialog = new DatabaseSetupDialog(unreachablePath: null, required: false);
            await dialog.ShowDialog(this);
        };
        MenuFileExit.Click += (_, _) => Close();
        MenuJobs.Click += (_, _) => NavigateTo("Jobs");
        MenuBatchGenerate.Click += (_, _) => NavigateTo("BatchGenerate");
        MenuTemplateLookup.Click += (_, _) => NavigateTo("TemplateLookup");
        MenuRefreshTemplates.Click     += (_, _) => NavigateTo("RefreshTemplates");
        MenuDeduplicateHardware.Click  += (_, _) => NavigateTo("DeduplicateHardware");
        MenuRelinkTemplates.Click     += async (_, _) => await RelinkTemplatesAsync();
        MenuDumpDatabase.Click         += async (_, _) => await DumpDatabaseAsync();
        MenuExportSqlServerScript.Click += async (_, _) => await ExportSqlServerScriptAsync();
        MenuImportLegacyDatabase.Click += async (_, _) => await ImportLegacyDatabaseAsync();
        MenuImportJobsWizard.Click += (_, _) => NavigateTo("JobImportWizard");
        MenuHardwareItems.Click += (_, _) => NavigateTo("HardwareItems");
        MenuTemplates.Click += (_, _) => NavigateTo("Templates");
        MenuCustomers.Click += (_, _) => NavigateTo("Customers");
        MenuManufacturers.Click += (_, _) => NavigateTo("Manufacturers");
        MenuDescriptions.Click += (_, _) => NavigateTo("Descriptions");
        MenuAppSettings.Click += (_, _) => NavigateTo("AppSettings");
        MenuProjectManagers.Click += (_, _) => NavigateTo("ProjectManagers");
        MenuUserProfiles.Click += (_, _) => NavigateTo("UserProfiles");
    }

    /// <summary>Navigates the content area to the named view and updates the status bar.</summary>
    /// <param name="viewName">The name of the view to display.</param>
    private void NavigateTo(string viewName)
    {
        // Parameterized route: "JobDetail:{id}"
        if (viewName.StartsWith("JobDetail:") &&
            int.TryParse(viewName.Substring("JobDetail:".Length), out var jobId))
        {
            var detail = new JobDetailView(jobId);
            detail.NavigationRequested += NavigateTo;
            ContentArea.Content = detail;
            var user = SessionService.ActiveUserProfile?.UserName;
            var suffix = user != null ? $"  —  {user}" : string.Empty;
            StatusText.Text = $"Job Detail{suffix}";
            return;
        }

        // Parameterized route: "BulkManufacturerSession:{jobId}"
        if (viewName.StartsWith("BulkManufacturerSession:") &&
            int.TryParse(viewName.Substring("BulkManufacturerSession:".Length), out var sessionJobId))
        {
            var session = new Views.BulkManufacturerSessionView(sessionJobId);
            session.NavigationRequested += NavigateTo;
            ContentArea.Content = session;
            var us = SessionService.ActiveUserProfile?.UserName;
            StatusText.Text = $"Bulk Hardware Entry{(us != null ? $"  —  {us}" : string.Empty)}";
            return;
        }

        // Parameterized route: "BulkManufacturerSelection:{jobId}"
        if (viewName.StartsWith("BulkManufacturerSelection:") &&
            int.TryParse(viewName["BulkManufacturerSelection:".Length..], out var bmsJobId))
        {
            var sel = new Views.BulkManufacturerSelectionView(bmsJobId);
            sel.NavigationRequested += NavigateTo;
            ContentArea.Content = sel;
            var uBms = SessionService.ActiveUserProfile?.UserName;
            StatusText.Text = $"Bulk Add — Select Manufacturers{(uBms != null ? $"  —  {uBms}" : string.Empty)}";
            return;
        }

        // Parameterized route: "BulkJobHub:{jobId}"
        if (viewName.StartsWith("BulkJobHub:") &&
            int.TryParse(viewName["BulkJobHub:".Length..], out var hubJobId))
        {
            var hub = new Views.BulkJobHubView(hubJobId);
            hub.NavigationRequested += NavigateTo;
            ContentArea.Content = hub;
            var uHub = SessionService.ActiveUserProfile?.UserName;
            StatusText.Text = $"Bulk Add — Hub{(uHub != null ? $"  —  {uHub}" : string.Empty)}";
            return;
        }

        // Parameterized route: "BulkHardwareEntry:{jobId}:{mfrId}"
        if (viewName.StartsWith("BulkHardwareEntry:"))
        {
            var parts = viewName["BulkHardwareEntry:".Length..].Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var bulkJobId) &&
                int.TryParse(parts[1], out var bulkMfrId))
            {
                var bulk = new Views.BulkHardwareEntryView(bulkJobId, bulkMfrId);
                bulk.NavigationRequested += NavigateTo;
                ContentArea.Content = bulk;
                var uBulk = SessionService.ActiveUserProfile?.UserName;
                StatusText.Text = $"Bulk Add — Items{(uBulk != null ? $"  —  {uBulk}" : string.Empty)}";
                return;
            }
        }

        // Parameterized route: "TemplateResolutionWizard:{id}"
        if (viewName.StartsWith("TemplateResolutionWizard:") &&
            int.TryParse(viewName.Substring("TemplateResolutionWizard:".Length), out var wizJobId))
        {
            var wizard = new Views.TemplateResolutionWizardView(wizJobId);
            wizard.NavigationRequested += NavigateTo;
            ContentArea.Content = wizard;
            var u3 = SessionService.ActiveUserProfile?.UserName;
            StatusText.Text = $"Template Resolution Wizard{(u3 != null ? $"  —  {u3}" : string.Empty)}";
            return;
        }

        // Parameterized route: "HardwareItemDetail:{id}" or "HardwareItemDetail:{id}:{returnRoute}"
        if (viewName.StartsWith("HardwareItemDetail:"))
        {
            var remainder = viewName.Substring("HardwareItemDetail:".Length);
            var splitIndex = remainder.IndexOf(':');
            var idPart = splitIndex < 0 ? remainder : remainder.Substring(0, splitIndex);
            var returnRoute = splitIndex < 0 ? null : remainder.Substring(splitIndex + 1);

            if (int.TryParse(idPart, out var itemId))
            {
                var detail = new HardwareItemDetailView(itemId, returnRoute);
                detail.NavigationRequested += NavigateTo;
                ContentArea.Content = detail;
                var user = SessionService.ActiveUserProfile?.UserName;
                var suffix = user != null ? $"  —  {user}" : string.Empty;
                StatusText.Text = $"Hardware Item Detail{suffix}";
                return;
            }
        }

        _router.NavigateTo(viewName);
        var u = SessionService.ActiveUserProfile?.UserName;
        var sfx = u != null ? $"  —  {u}" : string.Empty;
        StatusText.Text = viewName == "Dashboard" ? $"Ready{sfx}" : $"{viewName}{sfx}";
    }

    /// <summary>
    /// Re-attempts navigation to the "Jobs" view if the content area is still empty, i.e. the
    /// initial navigation in the constructor was skipped because the database connection
    /// wasn't configured yet. Safe to call unconditionally after startup.
    /// </summary>
    public void NavigateToJobsIfEmpty()
    {
        if (ContentArea.Content == null)
            NavigateTo("Jobs");
    }

    /// <summary>
    /// Prompts for a destination text file and dumps the full contents of every database
    /// table into it, for ad-hoc inspection or off-site backup.
    /// </summary>
    private async Task DumpDatabaseAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Dump Database To Text File",
            SuggestedFileName = $"database-dump-{System.DateTime.Now:yyyyMMdd-HHmmss}.txt",
            FileTypeChoices = new[] { new FilePickerFileType("Text File") { Patterns = new[] { "*.txt" } } }
        });
        if (file == null) return;

        try
        {
            using var context = DatabaseInitializer.CreateContext();
            var tableCount = DatabaseDumpService.DumpToTextFile(context, file.Path.LocalPath);
            await DialogHelper.ShowInfoAsync(this,
                $"Database dumped successfully.\n\n{tableCount} table(s) written to:\n{file.Path.LocalPath}",
                "Dump Database");
        }
        catch (System.Exception ex)
        {
            await DialogHelper.ShowInfoAsync(this, $"Error dumping database: {ex.Message}", "Dump Database");
        }
    }

    /// <summary>
    /// Prompts for a destination .sql file and exports the full schema and contents of the
    /// active database to a flat T-SQL script that can be run directly in SQL Server
    /// Management Studio against an empty/target database.
    /// </summary>
    private async Task ExportSqlServerScriptAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export SQL Server Script",
            SuggestedFileName = $"database-export-{System.DateTime.Now:yyyyMMdd-HHmmss}.sql",
            FileTypeChoices = new[] { new FilePickerFileType("SQL Script") { Patterns = new[] { "*.sql" } } }
        });
        if (file == null) return;

        try
        {
            using var context = DatabaseInitializer.CreateContext();
            var tableCount = await SqlServerScriptExportService.ExportAsync(context, file.Path.LocalPath);
            await DialogHelper.ShowInfoAsync(this,
                $"Database exported successfully.\n\n{tableCount} table(s) written to:\n{file.Path.LocalPath}",
                "Export SQL Server Script");
        }
        catch (System.Exception ex)
        {
            await DialogHelper.ShowInfoAsync(this, $"Error exporting database: {ex.Message}", "Export SQL Server Script");
        }
    }

    /// <summary>
    /// Prompts for a legacy/standalone SQLite database file, computes a dry-run merge plan
    /// against the master database (comparing Jobs and all related tables on their identity
    /// fields, ignoring timestamps), shows the plan for review, and — only after explicit
    /// confirmation — applies it. Conflicting Ids are remapped automatically, including every
    /// foreign key (e.g. JobHardware.JobId) that points at the affected rows.
    /// </summary>
    private async Task ImportLegacyDatabaseAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Legacy SQLite Database to Import",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Database") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" } },
                FilePickerFileTypes.All
            }
        });
        if (files == null || files.Count == 0) return;

        var sqlitePath = files[0].Path.LocalPath;
        var mergeService = new DatabaseMergeImportService(DatabaseInitializer.CreateContext);

        MergeImportPlan plan;
        try
        {
            plan = await mergeService.PreviewAsync(sqlitePath);
        }
        catch (System.Exception ex)
        {
            await DialogHelper.ShowInfoAsync(this, $"Error reading legacy database: {ex.Message}", "Import Legacy Database");
            return;
        }

        if (!plan.HasChanges)
        {
            await DialogHelper.ShowScrollableInfoAsync(this,
                "No new rows to import — every row in the legacy database already exists in the master database.\n\n" +
                string.Join("\n", plan.TablePreviews.Select(t => t.Summary)),
                "Import Legacy Database");
            return;
        }

        var summary = string.Join("\n", plan.TablePreviews.Select(t => t.Summary));
        var proceed = await DialogHelper.ConfirmAsync(this,
            $"The following changes will be imported from:\n{sqlitePath}\n\n{summary}\n\n" +
            $"Total rows to insert: {plan.TotalInsertCount}\n\n" +
            "Rows whose Id conflicts with a different existing record will be assigned a new Id, " +
            "and every related record (hardware links, releases, snapshots, attachments, etc.) will be " +
            "updated to reference the new Id automatically.\n\n" +
            "Proceed with the import?",
            "Confirm Import Legacy Database");
        if (!proceed) return;

        try
        {
            await mergeService.ApplyAsync(plan);
            await DialogHelper.ShowInfoAsync(this,
                $"Import completed successfully.\n\n{plan.TotalInsertCount} row(s) imported.",
                "Import Legacy Database");
        }
        catch (System.Exception ex)
        {
            await DialogHelper.ShowInfoAsync(this, $"Error applying import: {ex.Message}", "Import Legacy Database");
        }
    }

    /// <summary>
    /// Prompts for a legacy/standalone SQLite database file, matches hardware items and
    /// templates between it and the master database by Manufacturer + Description path +
    /// Model/Template number (ignoring Ids, which may differ), and restores any global
    /// hardware-item/template links present in the legacy file but missing in the master
    /// database. Shows a preview for review and requires explicit confirmation before
    /// writing. Never removes or duplicates existing links.
    /// </summary>
    private async Task RelinkTemplatesAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Legacy SQLite Database to Compare",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite Database") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" } },
                FilePickerFileTypes.All
            }
        });
        if (files == null || files.Count == 0) return;

        var sqlitePath = files[0].Path.LocalPath;
        var relinkService = new TemplateRelinkService(DatabaseInitializer.CreateContext);

        TemplateRelinkPlan plan;
        try
        {
            plan = await relinkService.PreviewAsync(sqlitePath);
        }
        catch (System.Exception ex)
        {
            await DialogHelper.ShowInfoAsync(this, $"Error reading legacy database: {ex.Message}", "Relink Templates");
            return;
        }

        if (!plan.HasChanges)
        {
            await DialogHelper.ShowScrollableInfoAsync(this,
                "No missing links found — every hardware-item/template link in the legacy database " +
                "that could be matched already exists in the master database.\n\n" +
                plan.Summary +
                (plan.UnmatchedHardwareItems.Count > 0
                    ? "\n\nUnmatched hardware items:\n" + string.Join("\n", plan.UnmatchedHardwareItems)
                    : string.Empty) +
                (plan.UnmatchedTemplates.Count > 0
                    ? "\n\nUnmatched templates:\n" + string.Join("\n", plan.UnmatchedTemplates)
                    : string.Empty),
                "Relink Templates");
            return;
        }

        var details = string.Join("\n", plan.ToAdd.Select(r => $"{r.HardwareItemDescription}  ->  {r.TemplateDescription}"));
        var unmatchedSection =
            (plan.UnmatchedHardwareItems.Count > 0
                ? $"\n\n{plan.UnmatchedHardwareItems.Count} legacy hardware item(s) could not be matched and were skipped:\n" + string.Join("\n", plan.UnmatchedHardwareItems)
                : string.Empty) +
            (plan.UnmatchedTemplates.Count > 0
                ? $"\n\n{plan.UnmatchedTemplates.Count} legacy template(s) could not be matched and were skipped:\n" + string.Join("\n", plan.UnmatchedTemplates)
                : string.Empty);

        var proceed = await DialogHelper.ConfirmAsync(this,
            $"Comparing against:\n{sqlitePath}\n\n" +
            $"{plan.ToAdd.Count} missing link(s) will be restored (only global links; job-scoped links are not touched):\n\n{details}" +
            unmatchedSection +
            "\n\nNo existing links will be changed or removed. Proceed?",
            "Confirm Relink Templates");
        if (!proceed) return;

        try
        {
            await relinkService.ApplyAsync(plan);
            await DialogHelper.ShowInfoAsync(this,
                $"Relink completed successfully.\n\n{plan.ToAdd.Count} link(s) restored.",
                "Relink Templates");
        }
        catch (System.Exception ex)
        {
            await DialogHelper.ShowInfoAsync(this, $"Error applying relink: {ex.Message}", "Relink Templates");
        }
    }

    /// <summary>Updates the status bar to reflect the newly chosen active user.</summary>
    /// <param name="userName">The user name to display.</param>
    public void SetActiveUser(string userName)
    {
        StatusText.Text = $"Ready  —  {userName}";
    }

    /// <summary>
    /// Re-syncs the currently displayed view's "creator/user" combo box to
    /// <see cref="SessionService.ActiveUserProfile"/>. Needed because the initial view is
    /// navigated to synchronously at startup, before the active user profile is resolved
    /// asynchronously in <c>App.axaml.cs</c>.
    /// </summary>
    public void RefreshActiveUserInCurrentView()
    {
        switch (ContentArea.Content)
        {
            case JobsView jobsView:
                jobsView.RefreshActiveUserSelection();
                break;
            case BatchGenerateView batchView:
                batchView.RefreshActiveUserSelection();
                break;
        }
    }
}