using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            mainWindow.Opened += async (_, _) =>
            {
                // Step 1: Resolve database file location (may show DatabaseSetupDialog).
                await ResolveDatabaseLocationAsync(mainWindow);

                // Step 2: Apply migrations / create schema at the now-confirmed location. All
                // machines connect directly to this shared file — there is no local working
                // copy to sync from/to anymore.
                DatabaseInitializer.Initialize();

                // Step 3: Auto-select the profile bound to this machine, or show picker.
                var machineId = MachineIdentityService.GetMachineId();

                using (var ctx = DatabaseInitializer.CreateContext())
                {
                    var bound = ctx.UserProfiles.FirstOrDefault(u => u.MachineId == machineId);
                    if (bound != null)
                        SessionService.ActiveUserProfile = bound;
                }

                if (SessionService.ActiveUserProfile == null)
                {
                    var picker = new ProfilePickerDialog(machineId);
                    await picker.ShowDialog(mainWindow);
                }

                // Step 4: Update the status bar and kick off background refresh.
                mainWindow.SetActiveUser(SessionService.ActiveUserProfile?.UserName ?? "Unknown");
                _ = RunStartupRefreshAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Ensures a valid, reachable database path is configured before the app tries to use it.
    /// <list type="bullet">
    ///   <item>No pointer file + default DB exists → proceeds immediately.</item>
    ///   <item>No pointer file + no default DB → shows <see cref="DatabaseSetupDialog"/> (required).</item>
    ///   <item>Pointer file exists + file reachable → proceeds immediately.</item>
    ///   <item>Pointer file exists + file unreachable → shows <see cref="DatabaseSetupDialog"/>
    ///     with an error banner explaining the previous path (required).</item>
    /// </list>
    /// </summary>
    private static async Task ResolveDatabaseLocationAsync(Window owner)
    {
        while (true)
        {
            var configuredPath = DatabaseLocationService.GetConfiguredPath();

            if (configuredPath == null)
            {
                // No custom location set — default AppData path is always valid on first run.
                return;
            }

            if (File.Exists(configuredPath))
                return; // Configured path is reachable.

            // Pointer file exists but the file is gone (network share offline, path moved, etc.).
            var reconnectDialog = new DatabaseSetupDialog(unreachablePath: configuredPath, required: true);
            await reconnectDialog.ShowDialog(owner);
            // Dialog may write a new path; loop to re-check.
        }
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
            var socketsHandler = new System.Net.Http.SocketsHttpHandler();
            socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            var http = new HttpClient(socketsHandler) { Timeout = TimeSpan.FromSeconds(60) };
            var service = new TemplateRefreshService(refreshCtx, http);
            await service.RefreshAsync(saveLocation);

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
