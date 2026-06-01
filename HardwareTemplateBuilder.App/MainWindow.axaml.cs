using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
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
        NavigateTo("Jobs");

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
        _router.Register("DoorMaterials",    () => { var v = new DoorMaterialsView();    v.NavigationRequested += NavigateTo; return v; });
        _router.Register("AppSettings",      () => { var v = new AppSettingsView();      v.NavigationRequested += NavigateTo; return v; });
        _router.Register("RefreshTemplates",     () => { var v = new RefreshTemplatesView();     v.NavigationRequested += NavigateTo; return v; });
        _router.Register("DeduplicateHardware", () => { var v = new DeduplicateHardwareView(); v.NavigationRequested += NavigateTo; return v; });
        _router.Register("BackupDatabase",      () => { var v = new BackupDatabaseView();      v.NavigationRequested += NavigateTo; return v; });
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
                "Built with .NET 8 · Avalonia UI · PDFsharp · QuestPDF · SQLite",
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
        MenuTemplateLookup.Click += (_, _) => NavigateTo("TemplateLookup");
        MenuRefreshTemplates.Click     += (_, _) => NavigateTo("RefreshTemplates");
        MenuDeduplicateHardware.Click  += (_, _) => NavigateTo("DeduplicateHardware");
        MenuBackupDatabase.Click       += (_, _) => NavigateTo("BackupDatabase");
        MenuHardwareItems.Click += (_, _) => NavigateTo("HardwareItems");
        MenuTemplates.Click += (_, _) => NavigateTo("Templates");
        MenuCustomers.Click += (_, _) => NavigateTo("Customers");
        MenuManufacturers.Click += (_, _) => NavigateTo("Manufacturers");
        MenuDescriptions.Click += (_, _) => NavigateTo("Descriptions");
        MenuAppSettings.Click += (_, _) => NavigateTo("AppSettings");
        MenuDoorMaterials.Click += (_, _) => NavigateTo("DoorMaterials");
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

        // Parameterized route: "HardwareItemDetail:{id}"
        if (viewName.StartsWith("HardwareItemDetail:") &&
            int.TryParse(viewName.Substring("HardwareItemDetail:".Length), out var itemId))
        {
            var detail = new HardwareItemDetailView(itemId);
            detail.NavigationRequested += NavigateTo;
            ContentArea.Content = detail;
            var user = SessionService.ActiveUserProfile?.UserName;
            var suffix = user != null ? $"  —  {user}" : string.Empty;
            StatusText.Text = $"Hardware Item Detail{suffix}";
            return;
        }

        _router.NavigateTo(viewName);
        var u = SessionService.ActiveUserProfile?.UserName;
        var sfx = u != null ? $"  —  {u}" : string.Empty;
        StatusText.Text = viewName == "Dashboard" ? $"Ready{sfx}" : $"{viewName}{sfx}";
    }

    /// <summary>Updates the status bar to reflect the newly chosen active user.</summary>
    /// <param name="userName">The user name to display.</param>
    public void SetActiveUser(string userName)
    {
        StatusText.Text = $"Ready  —  {userName}";
    }
}