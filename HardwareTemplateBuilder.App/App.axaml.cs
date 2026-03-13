using System;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HardwareTemplateBuilder.App.Views;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;

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
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Show the profile picker as soon as the main window opens.
            // The picker cannot be dismissed without selecting or creating a profile.
            mainWindow.Opened += async (_, _) =>
            {
                var picker = new ProfilePickerDialog();
                await picker.ShowDialog(mainWindow);

                // Update the status bar with the chosen user.
                mainWindow.SetActiveUser(SessionService.ActiveUserProfile?.UserName ?? "Unknown");

                // Auto-refresh: silently download all templates if >7 days since last refresh.
                _ = RunStartupRefreshAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Checks <c>AppSettings["LastRefreshTimestamp"]</c> and runs a full template refresh in
    /// the background if the value is missing or older than 7 days.
    /// Failures are swallowed — auto-refresh is best-effort.
    /// </summary>
    private static async Task RunStartupRefreshAsync()
    {
        try
        {
            string saveLocation;
            bool needsRefresh;

            using (var ctx = DatabaseInitializer.CreateContext())
            {
                var settings = new AppSettingRepository(ctx);
                var raw = settings.GetValue("LastRefreshTimestamp");
                needsRefresh = string.IsNullOrWhiteSpace(raw)
                    || !DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                    || (DateTime.UtcNow - last).TotalDays > 7;

                var configured = settings.GetValue("TemplateStorageLocation");
                saveLocation = !string.IsNullOrWhiteSpace(configured)
                    ? configured
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            if (!needsRefresh) return;

            using var refreshCtx = DatabaseInitializer.CreateContext();
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var service = new TemplateRefreshService(refreshCtx, http);
            await service.RefreshAsync(saveLocation);

            // Record the timestamp so the next launch skips the auto-refresh.
            using var tsCtx = DatabaseInitializer.CreateContext();
            new AppSettingRepository(tsCtx).SetValue(
                "LastRefreshTimestamp", DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Silent: auto-refresh failures must not disrupt the user's session.
        }
    }
}
