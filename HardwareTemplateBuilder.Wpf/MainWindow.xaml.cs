using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Navigation;
using HardwareTemplateBuilder.Wpf.Ui;
using HardwareTemplateBuilder.Wpf.Views;

namespace HardwareTemplateBuilder.Wpf;

/// <summary>
/// The application's main window: menu bar, navigation content area and status bar.
/// Routing matches the Avalonia app's <c>MainWindow</c> exactly (same route names,
/// same <c>"ViewName:{id}"</c> parameterized routes).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The view router managing content area navigation.</summary>
    private readonly ViewRouter _router;

    /// <summary>Ticks the status-bar clock, like the Win95 taskbar tray.</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };

    /// <summary>Initializes the main window and registers all views.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _router = new ViewRouter(ContentArea);
        RegisterViews();

        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("h:mm tt");
        ClockText.Text = DateTime.Now.ToString("h:mm tt");
        _clock.Start();

        // Do NOT navigate here: views query the database as soon as they load, and the
        // connection is only verified in App.RunStartupAsync, which calls NavigateToJobsIfEmpty.

        // Missing-template alert check at startup (best-effort, background).
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
        _router.Register("Dashboard",           () => { var v = new DashboardView();           v.NavigationRequested += NavigateTo; return v; });
        _router.Register("TemplateLookup",      () => { var v = new TemplateLookupView();      v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Jobs",                () => { var v = new JobsView();                v.NavigationRequested += NavigateTo; return v; });
        _router.Register("HardwareItems",       () => { var v = new HardwareItemsView();       v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Templates",           () => { var v = new IndividualTemplatesView(); v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Customers",           () => { var v = new CustomersView();           v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Manufacturers",       () => { var v = new ManufacturersView();       v.NavigationRequested += NavigateTo; return v; });
        _router.Register("Descriptions",        () => { var v = new DescriptionsView();        v.NavigationRequested += NavigateTo; return v; });
        _router.Register("ProjectManagers",     () => { var v = new ProjectManagersView();     v.NavigationRequested += NavigateTo; return v; });
        _router.Register("UserProfiles",        () => { var v = new UserProfilesView();        v.NavigationRequested += NavigateTo; return v; });
        _router.Register("AppSettings",         () => { var v = new AppSettingsView();         v.NavigationRequested += NavigateTo; return v; });
        _router.Register("BatchGenerate",       () => { var v = new BatchGenerateView();       v.NavigationRequested += NavigateTo; return v; });
        _router.Register("TransferJob",         () => { var v = new TransferJobView();         v.NavigationRequested += NavigateTo; return v; });
        _router.Register("RefreshTemplates",    () => { var v = new RefreshTemplatesView();    v.NavigationRequested += NavigateTo; return v; });
        _router.Register("DeduplicateHardware", () => { var v = new DeduplicateHardwareView(); v.NavigationRequested += NavigateTo; return v; });
        _router.Register("JobImportWizard",     () => { var v = new JobImportWizardView();     v.NavigationRequested += NavigateTo; return v; });
    }

    /// <summary>Menu items whose Tag names a route navigate to it.</summary>
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string route })
            NavigateTo(route);
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        await DialogHelper.ShowInfoAsync(this,
            $"Hardware Template Builder 95  v{verStr}\n\n" +
            "Manages door hardware templates and generates PDF packages for job orders.\n\n" +
            "Built with .NET 8 · WPF · PDFsharp · QuestPDF",
            "About Hardware Template Builder");
    }

    private async void ChangeUser_Click(object sender, RoutedEventArgs e)
    {
        var machineId = MachineIdentityService.GetMachineId();
        // Clear the active profile so the picker cannot be dismissed without re-selecting.
        SessionService.ActiveUserProfile = null;
        var picker = new ProfilePickerDialog(machineId);
        await picker.ShowDialogAsync(this);
        SetActiveUser(SessionService.ActiveUserProfile?.UserName ?? "Unknown");
        RefreshActiveUserInCurrentView();
    }

    private async void DatabaseLocation_Click(object sender, RoutedEventArgs e)
    {
        // Not required: the user can cancel without changing the location.
        var dialog = new DatabaseSetupDialog(unreachablePath: null, required: false);
        await dialog.ShowDialogAsync(this);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private async void RelinkTemplates_Click(object sender, RoutedEventArgs e) => await RelinkTemplatesAsync();
    private async void DumpDatabase_Click(object sender, RoutedEventArgs e) => await DumpDatabaseAsync();
    private async void ExportSqlServerScript_Click(object sender, RoutedEventArgs e) => await ExportSqlServerScriptAsync();
    private async void ImportLegacyDatabase_Click(object sender, RoutedEventArgs e) => await ImportLegacyDatabaseAsync();

    /// <summary>Navigates the content area to the named (optionally parameterized) route.</summary>
    /// <param name="viewName">A registered view name, or a <c>"ViewName:{id}"</c> route.</param>
    private void NavigateTo(string viewName)
    {
        if (TryNavigateParameterized(viewName)) return;

        _router.NavigateTo(viewName);
        SetStatusWithUser(viewName == "Dashboard" ? "Ready" : SplitWords(viewName));
    }

    /// <summary>Handles the parameterized routes; returns false for plain view names.</summary>
    private bool TryNavigateParameterized(string viewName)
    {
        UserControl? view = null;
        string? status = null;

        if (TryParseId(viewName, "JobDetail:", out var jobId))
        {
            var v = new JobDetailView(jobId); v.NavigationRequested += NavigateTo;
            (view, status) = (v, "Job Detail");
        }
        else if (TryParseId(viewName, "BulkManufacturerSession:", out var sessionJobId))
        {
            var v = new BulkManufacturerSessionView(sessionJobId); v.NavigationRequested += NavigateTo;
            (view, status) = (v, "Bulk Hardware Entry");
        }
        else if (TryParseId(viewName, "BulkManufacturerSelection:", out var bmsJobId))
        {
            var v = new BulkManufacturerSelectionView(bmsJobId); v.NavigationRequested += NavigateTo;
            (view, status) = (v, "Bulk Add — Select Manufacturers");
        }
        else if (TryParseId(viewName, "BulkJobHub:", out var hubJobId))
        {
            var v = new BulkJobHubView(hubJobId); v.NavigationRequested += NavigateTo;
            (view, status) = (v, "Bulk Add — Hub");
        }
        else if (viewName.StartsWith("BulkHardwareEntry:"))
        {
            var parts = viewName["BulkHardwareEntry:".Length..].Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var bulkJobId) && int.TryParse(parts[1], out var bulkMfrId))
            {
                var v = new BulkHardwareEntryView(bulkJobId, bulkMfrId); v.NavigationRequested += NavigateTo;
                (view, status) = (v, "Bulk Add — Items");
            }
        }
        else if (TryParseId(viewName, "TemplateResolutionWizard:", out var wizJobId))
        {
            var v = new TemplateResolutionWizardView(wizJobId); v.NavigationRequested += NavigateTo;
            (view, status) = (v, "Template Resolution Wizard");
        }
        else if (viewName.StartsWith("HardwareItemDetail:"))
        {
            // "HardwareItemDetail:{id}" or "HardwareItemDetail:{id}:{returnRoute}"
            var remainder = viewName["HardwareItemDetail:".Length..];
            var splitIndex = remainder.IndexOf(':');
            var idPart = splitIndex < 0 ? remainder : remainder[..splitIndex];
            var returnRoute = splitIndex < 0 ? null : remainder[(splitIndex + 1)..];
            if (int.TryParse(idPart, out var itemId))
            {
                var v = new HardwareItemDetailView(itemId, returnRoute); v.NavigationRequested += NavigateTo;
                (view, status) = (v, "Hardware Item Detail");
            }
        }

        if (view == null) return false;

        ContentArea.Content = view;
        SetStatusWithUser(status!);
        return true;
    }

    private static bool TryParseId(string route, string prefix, out int id)
    {
        id = 0;
        return route.StartsWith(prefix) && int.TryParse(route[prefix.Length..], out id);
    }

    /// <summary>"DeduplicateHardware" → "Deduplicate Hardware" for the status bar.</summary>
    private static string SplitWords(string name)
        => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()));

    private void SetStatusWithUser(string text) => StatusText.Text = text;

    /// <summary>Shows a message in the left status-bar field.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// Navigates to "Jobs" if the content area is still empty, i.e. on first startup once the
    /// database connection has been resolved. Safe to call unconditionally.
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
        var file = FilePickers.SaveFile(this, "Dump Database To Text File",
            $"database-dump-{System.DateTime.Now:yyyyMMdd-HHmmss}.txt", "Text File (*.txt)|*.txt");
        if (file == null) return;

        try
        {
            using var context = DatabaseInitializer.CreateContext();
            var tableCount = DatabaseDumpService.DumpToTextFile(context, file);
            await DialogHelper.ShowInfoAsync(this,
                $"Database dumped successfully.\n\n{tableCount} table(s) written to:\n{file}",
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
        var file = FilePickers.SaveFile(this, "Export SQL Server Script",
            $"database-export-{System.DateTime.Now:yyyyMMdd-HHmmss}.sql", "SQL Script (*.sql)|*.sql");
        if (file == null) return;

        try
        {
            using var context = DatabaseInitializer.CreateContext();
            var tableCount = await SqlServerScriptExportService.ExportAsync(context, file);
            await DialogHelper.ShowInfoAsync(this,
                $"Database exported successfully.\n\n{tableCount} table(s) written to:\n{file}",
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
        var sqlitePath = FilePickers.OpenFile(this, "Select Legacy SQLite Database to Import", FilePickers.SqliteFilter);
        if (sqlitePath == null) return;
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
        var sqlitePath = FilePickers.OpenFile(this, "Select Legacy SQLite Database to Compare", FilePickers.SqliteFilter);
        if (sqlitePath == null) return;
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
        UserStatusText.Text = userName;
        if (StatusText.Text.StartsWith("Connecting") || StatusText.Text == "Ready")
            StatusText.Text = "Ready";
    }

    /// <summary>
    /// Re-syncs the current view's "creator/user" combo to <see cref="SessionService.ActiveUserProfile"/>.
    /// Needed because the first view loads before the active profile is resolved.
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
