using Avalonia.Controls;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>The main dashboard view showing navigation buttons to all feature areas.</summary>
public partial class DashboardView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes a new instance of <see cref="DashboardView"/>.</summary>
    public DashboardView()
    {
        InitializeComponent();
        WireButtons();
    }

    private void WireButtons()
    {
        BtnTemplateLookup.Click += (_, _) => NavigationRequested?.Invoke("TemplateLookup");
        BtnJobs.Click += (_, _) => NavigationRequested?.Invoke("Jobs");
        BtnHardwareItems.Click += (_, _) => NavigationRequested?.Invoke("HardwareItems");
        BtnTemplates.Click += (_, _) => NavigationRequested?.Invoke("Templates");
        BtnCustomers.Click += (_, _) => NavigationRequested?.Invoke("Customers");
        BtnManufacturers.Click += (_, _) => NavigationRequested?.Invoke("Manufacturers");
        BtnDescriptions.Click += (_, _) => NavigationRequested?.Invoke("Descriptions");
        BtnProjectManagers.Click += (_, _) => NavigationRequested?.Invoke("ProjectManagers");
        BtnUserProfiles.Click += (_, _) => NavigationRequested?.Invoke("UserProfiles");
        BtnWeights.Click += (_, _) => NavigationRequested?.Invoke("Weights");
        BtnDoorMaterials.Click += (_, _) => NavigationRequested?.Invoke("DoorMaterials");
        BtnRefreshTemplates.Click += (_, _) => NavigationRequested?.Invoke("RefreshTemplates");
    }
}
