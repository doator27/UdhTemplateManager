using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HardwareTemplateBuilder.Core.Data;
using System.Linq;

namespace HardwareTemplateBuilder.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DatabaseInitializer.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();

            // On first launch (no user profiles), navigate to User Profiles to prompt creation
            using var context = DatabaseInitializer.CreateContext();
            if (context.UserProfiles.Count() == 0)
                window.NavigateToFirstLaunch();

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
