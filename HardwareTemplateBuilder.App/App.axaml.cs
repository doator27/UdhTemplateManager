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
                // Step 1: (Reserved) master database location resolution hook.
                await ResolveDatabaseLocationAsync(mainWindow);

                // Step 2: Apply migrations / create schema for the local, per-machine working
                // copy of the database.
                DatabaseInitializer.Initialize();

                // Step 2b: Sync the local working copy with the master database, if configured
                // and reachable — new/changed local rows are merged into master, then the local
                // file is refreshed with a clean copy of master. If the master is unreachable,
                // this is a no-op and the app continues working offline against the local copy.
                await Task.Run(() =>
                    MasterSyncService.Sync(DatabaseInitializer.GetMasterPath(), DatabaseInitializer.GetDatabasePath()));

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
    /// Reports whether a master database path is configured; no longer blocks startup when the
    /// master is unreachable — the app always has a usable local working copy
    /// (<see cref="DatabaseInitializer.GetDatabasePath"/>) and syncs with master best-effort via
    /// <see cref="MasterSyncService"/>. Users can still open <see cref="DatabaseSetupDialog"/>
    /// from the File menu to configure or change the master location at any time.
    /// </summary>
    private static Task ResolveDatabaseLocationAsync(Window owner) => Task.CompletedTask;


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
