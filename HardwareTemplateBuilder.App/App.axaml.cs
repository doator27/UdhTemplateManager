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
        Helpers.ThemeService.ApplySaved();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            mainWindow.Opened += async (_, _) =>
            {
                try
                {
                    // Step 1: Ensure a SQL Server connection is configured; prompt if not.
                    await ResolveDatabaseLocationAsync(mainWindow);

                    // Step 2: Apply migrations / create schema on the shared, centralized
                    // SQL Server database used by all clients.
                    await Task.Run(DatabaseInitializer.Initialize);

                    // The initial "Jobs" navigation in MainWindow's constructor may have been
                    // skipped if the database wasn't configured yet at that point. Retry it
                    // now that a valid connection is guaranteed.
                    mainWindow.NavigateToJobsIfEmpty();

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
                    mainWindow.RefreshActiveUserInCurrentView();
                    _ = RunStartupRefreshAsync();
                }
                catch (Exception ex)
                {
                    // Any unhandled exception here previously left the main window open but
                    // completely blank/unresponsive-looking forever (async void has nowhere to
                    // propagate to except a background dispatcher continuation), which is
                    // indistinguishable from the app "locking up" during launch. Log it and tell
                    // the user plainly instead of failing silently.
                    Helpers.CrashLogger.Log(ex, isTerminating: false);

                    var message = ex is Microsoft.Data.SqlClient.SqlException or System.Net.Sockets.SocketException
                        ? "Hardware Template Builder could not connect to the SQL Server database " +
                          "(Server=localhost;Database=templatedb). Verify the SQL Server instance is running " +
                          $"and reachable, then restart the application. Details: {ex.Message}"
                        : "Hardware Template Builder ran into a problem while starting up and could not finish " +
                          "loading. The error has been recorded in CrashReports.txt next to the application. " +
                          $"Details: {ex.Message}";

                    await Helpers.DialogHelper.ShowInfoAsync(mainWindow, message, "Startup error");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Ensures a SQL Server connection string is configured, prompting the user via
    /// <see cref="DatabaseSetupDialog"/> if one has not yet been set up on this machine.
    /// </summary>
    private static Task ResolveDatabaseLocationAsync(Window owner)
    {
        // Connection string is hardcoded in DatabaseInitializer.GetConnectionString(),
        // so no setup prompt is needed.
        return Task.CompletedTask;
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
