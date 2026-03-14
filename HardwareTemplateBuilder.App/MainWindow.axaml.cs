using System.Reflection;
using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.App.Navigation;
using HardwareTemplateBuilder.App.Views;

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
        NavigateTo("Dashboard");
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
        _router.Register("RefreshTemplates", () => { var v = new RefreshTemplatesView(); v.NavigationRequested += NavigateTo; return v; });
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
        MenuFileExit.Click += (_, _) => Close();
        MenuJobs.Click += (_, _) => NavigateTo("Jobs");
        MenuTemplateLookup.Click += (_, _) => NavigateTo("TemplateLookup");
        MenuRefreshTemplates.Click += (_, _) => NavigateTo("RefreshTemplates");
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