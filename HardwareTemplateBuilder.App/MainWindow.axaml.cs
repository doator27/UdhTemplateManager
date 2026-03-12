using Avalonia.Controls;
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
        _router.Register("TemplateLookup", () => new TemplateLookupView());
        _router.Register("Jobs", () => new JobsView());
        _router.Register("HardwareItems", () => new HardwareItemsView());
        _router.Register("Templates", () => new IndividualTemplatesView());
        _router.Register("Customers", () => new CustomersView());
        _router.Register("Manufacturers", () => new ManufacturersView());
        _router.Register("Descriptions", () => new DescriptionsView());
        _router.Register("ProjectManagers", () => new ProjectManagersView());
        _router.Register("UserProfiles", () => new UserProfilesView());
        _router.Register("Weights", () => new WeightsView());
        _router.Register("DoorMaterials", () => new DoorMaterialsView());
        _router.Register("RefreshTemplates", () => new RefreshTemplatesView());
    }

    /// <summary>Wires all menu item click events to their corresponding navigation targets.</summary>
    private void WireMenuItems()
    {
        MenuFileExit.Click += (_, _) => Close();
        MenuJobs.Click += (_, _) => NavigateTo("Jobs");
        MenuRefreshTemplates.Click += (_, _) => NavigateTo("RefreshTemplates");
        MenuHardwareItems.Click += (_, _) => NavigateTo("HardwareItems");
        MenuTemplates.Click += (_, _) => NavigateTo("Templates");
        MenuCustomers.Click += (_, _) => NavigateTo("Customers");
        MenuManufacturers.Click += (_, _) => NavigateTo("Manufacturers");
        MenuDescriptions.Click += (_, _) => NavigateTo("Descriptions");
        MenuDoorMaterials.Click += (_, _) => NavigateTo("DoorMaterials");
        MenuWeights.Click += (_, _) => NavigateTo("Weights");
        MenuProjectManagers.Click += (_, _) => NavigateTo("ProjectManagers");
        MenuUserProfiles.Click += (_, _) => NavigateTo("UserProfiles");
    }

    /// <summary>Navigates the content area to the named view and updates the status bar.</summary>
    /// <param name="viewName">The name of the view to display.</param>
    private void NavigateTo(string viewName)
    {
        _router.NavigateTo(viewName);
        StatusText.Text = viewName == "Dashboard" ? "Ready" : viewName;
    }

    /// <summary>
    /// Navigates to the User Profiles view on first launch when no profiles exist.
    /// </summary>
    public void NavigateToFirstLaunch()
    {
        NavigateTo("UserProfiles");
        StatusText.Text = "Welcome! Please create a User Profile before proceeding.";
    }
}