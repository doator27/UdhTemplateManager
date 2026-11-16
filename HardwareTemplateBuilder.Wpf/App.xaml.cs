using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Views;

namespace HardwareTemplateBuilder.Wpf;

/// <summary>
/// WPF application. Mirrors <c>HardwareTemplateBuilder.App.App</c>'s startup sequence:
/// show the main window, then (once it has rendered) initialize the database, pick the user
/// profile, and kick off the background template refresh.
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogger.Log(args.Exception, isTerminating: false);
            MessageBox.Show(
                $"An unexpected error occurred and has been recorded in CrashReports.txt.\n\n{args.Exception.Message}",
                "Hardware Template Builder", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        var started = false;
        mainWindow.ContentRendered += async (_, _) =>
        {
            if (started) return;
            started = true;
            await RunStartupAsync(mainWindow);
        };

        mainWindow.Show();
    }

    private static async Task RunStartupAsync(MainWindow mainWindow)
    {
        try
        {
            mainWindow.SetStatus("Connecting to the database...");

            // Apply migrations / create schema on the shared, centralized database.
            await Task.Run(DatabaseInitializer.Initialize);

            // Views query the database as soon as they load, so the first navigation waits
            // until the connection is known to be good.
            mainWindow.NavigateToJobsIfEmpty();

            // Auto-select the profile bound to this machine, or show the picker.
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
                await picker.ShowDialogAsync(mainWindow);
            }

            mainWindow.SetActiveUser(SessionService.ActiveUserProfile?.UserName ?? "Unknown");
            mainWindow.RefreshActiveUserInCurrentView();
            _ = RunStartupRefreshAsync();
        }
        catch (Exception ex)
        {
            // Without this the window would stay open but blank, which looks like a hang.
            CrashLogger.Log(ex, isTerminating: false);

            var message = ex is Microsoft.Data.SqlClient.SqlException or System.Net.Sockets.SocketException
                ? "Hardware Template Builder could not connect to the database. Verify the server is running " +
                  $"and reachable, then restart the application. Details: {ex.Message}"
                : "Hardware Template Builder ran into a problem while starting up and could not finish " +
                  "loading. The error has been recorded in CrashReports.txt next to the application. " +
                  $"Details: {ex.Message}";

            mainWindow.SetStatus("Startup failed");
            await DialogHelper.ShowInfoAsync(mainWindow, message, "Startup error");
        }
    }

    /// <summary>
    /// Runs a full template refresh in the background when <c>LastRefreshTimestamp</c> is
    /// missing or older than 7 days. Failures are swallowed; auto-refresh is best-effort.
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
            var socketsHandler = new SocketsHttpHandler();
            socketsHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            var http = new HttpClient(socketsHandler) { Timeout = TimeSpan.FromSeconds(60) };
            var service = new TemplateRefreshService(refreshCtx, http);
            await service.RefreshAsync(saveLocation);

            using var tsCtx = DatabaseInitializer.CreateContext();
            new AppSettingRepository(tsCtx).SetValue("LastRefreshTimestamp", DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Silent: auto-refresh failures must not disrupt the user's session.
        }
    }
}
