using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// View that bulk-downloads all templates with an online link, updates their local file
/// path on success, and surfaces failures for manual follow-up. Corresponds to Phase 11.
/// </summary>
public partial class RefreshTemplatesView : UserControl
{
    private CancellationTokenSource? _cts;
    private string? _reportPath;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view and wires events on load.</summary>
    public RefreshTemplatesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        StartButton.Click  += async (_, _) => await OnStartRefreshAsync();
        CancelButton.Click += (_, _) => _cts?.Cancel();
        OpenReportButton.Click += (_, _) => OpenReport();
    }

    // ---------- Refresh ----------

    private async Task OnStartRefreshAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Resolve save location from global App Settings.
        string saveLocation;
        int templateCount;
        using (var ctx = DatabaseInitializer.CreateContext())
        {
            var settingsRepo = new AppSettingRepository(ctx);
            var configured = settingsRepo.GetValue("TemplateStorageLocation");
            saveLocation = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            templateCount = ctx.IndividualTemplates
                .Count(t => t.OnlineLink != null && t.OnlineLink != string.Empty);
        }

        if (templateCount == 0)
        {
            StatusLabel.Text = "No templates have an Online Link set. Nothing to refresh.";
            return;
        }

        SetRunningState(running: true);
        StatusLabel.Text    = string.Empty;
        ResultsPanel.IsVisible = false;

        RefreshResult? result;
        try
        {
            result = await Task.Run(async () =>
            {
                using var ctx    = DatabaseInitializer.CreateContext();
                var httpHandler = new System.Net.Http.SocketsHttpHandler();
                httpHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                var httpClient  = new HttpClient(httpHandler) { Timeout = TimeSpan.FromSeconds(60) };
                var service      = new TemplateRefreshService(ctx, httpClient);

                var progress = new Progress<RefreshProgress>(p =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        ProgressText.Text =
                            $"Downloading {p.Current} of {p.Total}: [ID {p.TemplateId}] {p.TemplateName}";
                    }));

                return await service.RefreshAsync(saveLocation, progress, ct);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Refresh cancelled.";
            SetRunningState(running: false);
            return;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Unexpected error: {ex.Message}";
            SetRunningState(running: false);
            return;
        }

        SetRunningState(running: false);

        _reportPath = result.Failures.Count > 0
            ? WriteFailureReport(saveLocation, result)
            : null;

        ShowResults(result);
    }

    // ---------- UI helpers ----------

    /// <summary>
    /// Switches the view between running and idle states:
    /// enables/disables buttons, shows/hides the progress panel.
    /// </summary>
    private void SetRunningState(bool running)
    {
        StartButton.IsEnabled   = !running;
        CancelButton.IsEnabled  = running;
        ProgressPanel.IsVisible = running;

        if (!running)
            ProgressText.Text = string.Empty;
    }

    /// <summary>Populates and reveals the results panel after a completed refresh.</summary>
    private void ShowResults(RefreshResult result)
    {
        SummaryLabel.Text = result.TotalAttempted == 0
            ? "No templates with online links were found."
            : $"{result.SuccessCount} of {result.TotalAttempted} template(s) updated successfully.";

        if (result.Failures.Count > 0)
        {
            FailuresLabel.Text     = $"{result.Failures.Count} failure(s):";
            FailuresList.ItemsSource = result.Failures
                .Select(f => $"[ID {f.TemplateId}] {f.TemplateName}: {f.ErrorMessage}")
                .ToList();
            FailuresPanel.IsVisible   = true;
            NoFailuresLabel.IsVisible = false;

            ReportPathLabel.Text    = _reportPath != null ? $"Report saved to: {_reportPath}" : string.Empty;
            OpenReportButton.IsVisible = _reportPath != null;
        }
        else
        {
            FailuresPanel.IsVisible   = false;
            NoFailuresLabel.IsVisible = result.TotalAttempted > 0;
        }

        ResultsPanel.IsVisible = true;
        StatusLabel.Text = result.Failures.Count == 0
            ? "Refresh complete."
            : $"Refresh complete with {result.Failures.Count} failure(s). See list below.";
    }

    /// <summary>
    /// Writes a timestamped text report listing every failed template — ID, name, online link,
    /// and the full error — to <paramref name="saveLocation"/> so problem templates can be
    /// reviewed and fixed outside the app. Best-effort: a write failure is reported in
    /// <see cref="StatusLabel"/> rather than throwing, since the refresh itself already succeeded.
    /// </summary>
    private string? WriteFailureReport(string saveLocation, RefreshResult result)
    {
        try
        {
            var reportPath = Path.Combine(saveLocation,
                $"TemplateRefreshFailures_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Template Refresh Report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"{result.SuccessCount} of {result.TotalAttempted} template(s) updated successfully.");
            sb.AppendLine($"{result.Failures.Count} failure(s):");
            sb.AppendLine();

            foreach (var f in result.Failures)
            {
                sb.AppendLine($"[ID {f.TemplateId}] {f.TemplateName}");
                sb.AppendLine($"  Online Link: {f.OnlineLink}");
                sb.AppendLine($"  Error: {f.ErrorMessage}");
                sb.AppendLine();
            }

            Directory.CreateDirectory(saveLocation);
            File.WriteAllText(reportPath, sb.ToString());
            return reportPath;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Refresh complete, but the failure report could not be written: {ex.Message}";
            return null;
        }
    }

    private void OpenReport()
    {
        if (_reportPath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_reportPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not open the report: {ex.Message}";
        }
    }
}
