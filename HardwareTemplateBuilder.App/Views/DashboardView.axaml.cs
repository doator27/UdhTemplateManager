using Avalonia.Controls;
using HardwareTemplateBuilder.App;

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
        Loaded += (_, _) => OnLoaded();
        WireButtons();
    }

    private void OnLoaded()
    {
        var user = SessionService.ActiveUserProfile?.UserName;
        WelcomeLabel.Text = user != null ? $"Welcome, {user}." : string.Empty;
    }

    private void WireButtons()
    {
        BtnJobs.Click              += (_, _) => NavigationRequested?.Invoke("Jobs");
        BtnTemplateLookup.Click    += (_, _) => NavigationRequested?.Invoke("TemplateLookup");
        BtnHardwareItems.Click     += (_, _) => NavigationRequested?.Invoke("HardwareItems");
        BtnTemplates.Click         += (_, _) => NavigationRequested?.Invoke("Templates");
        BtnCustomers.Click         += (_, _) => NavigationRequested?.Invoke("Customers");
        BtnManufacturers.Click     += (_, _) => NavigationRequested?.Invoke("Manufacturers");
        BtnDescriptions.Click      += (_, _) => NavigationRequested?.Invoke("Descriptions");
        BtnProjectManagers.Click   += (_, _) => NavigationRequested?.Invoke("ProjectManagers");
        BtnUserProfiles.Click      += (_, _) => NavigationRequested?.Invoke("UserProfiles");
        BtnAppSettings.Click       += (_, _) => NavigationRequested?.Invoke("AppSettings");
        BtnRefreshTemplates.Click  += (_, _) => NavigationRequested?.Invoke("RefreshTemplates");
    }
}
