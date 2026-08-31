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
    private const string AvgSecondsPerTemplateKey = "RefreshHistoryAvgSecondsPerTemplate";
    private const string HistoryTemplateCountKey   = "RefreshHistoryTemplateCount";

    private CancellationTokenSource? _cts;
    private string? _reportPath;
    private string? _timingReportPath;
    private DispatcherTimer? _elapsedTimer;
    private Stopwatch? _elapsedStopwatch;

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
        OpenTimingReportButton.Click += (_, _) => OpenTimingReport();
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

        var estimateSeconds = EstimateDuration(templateCount);
        EstimateLabel.Text = estimateSeconds.HasValue
            ? $"Estimated time: ~{FormatDuration(estimateSeconds.Value)} (based on {templateCount} template(s))"
            : string.Empty;

        StartElapsedTimer();

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
            StopElapsedTimer();
            StatusLabel.Text = "Refresh cancelled.";
            SetRunningState(running: false);
            return;
        }
        catch (Exception ex)
        {
            StopElapsedTimer();
            StatusLabel.Text = $"Unexpected error: {ex.Message}";
            SetRunningState(running: false);
            return;
        }

        StopElapsedTimer();
        SetRunningState(running: false);

        UpdateDurationHistory(result);

        _reportPath = result.Failures.Count > 0
            ? WriteFailureReport(saveLocation, result)
            : null;

        _timingReportPath = WriteTimingReport(saveLocation, result);

        ShowResults(result);
    }

    // ---------- Timing / estimation ----------

    /// <summary>Starts a UI timer that updates <see cref="ElapsedText"/> once per second.</summary>
    private void StartElapsedTimer()
    {
        _elapsedStopwatch = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
            ElapsedText.Text = $"Elapsed: {FormatDuration(_elapsedStopwatch!.Elapsed.TotalSeconds)}";
        _elapsedTimer.Start();
    }

    /// <summary>Stops the elapsed UI timer started by <see cref="StartElapsedTimer"/>.</summary>
    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
        _elapsedStopwatch?.Stop();
    }

    /// <summary>
    /// Estimates how long a refresh of <paramref name="templateCount"/> template(s) will take,
    /// based on the running average seconds-per-template recorded from prior refreshes.
    /// Returns null if no history has been recorded yet.
    /// </summary>
    private double? EstimateDuration(int templateCount)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var settingsRepo = new AppSettingRepository(ctx);
        var avgRaw = settingsRepo.GetValue(AvgSecondsPerTemplateKey);

        if (!double.TryParse(avgRaw, System.Globalization.CultureInfo.InvariantCulture, out var avgPerTemplate)
            || avgPerTemplate <= 0)
            return null;

        return avgPerTemplate * templateCount;
    }

    /// <summary>
    /// Updates the persisted running average seconds-per-template using this run's results,
    /// weighted by the number of templates successfully downloaded so the estimate becomes more
    /// accurate over time.
    /// </summary>
    private void UpdateDurationHistory(RefreshResult result)
    {
        if (result.SuccessCount == 0)
            return;

        using var ctx = DatabaseInitializer.CreateContext();
        var settingsRepo = new AppSettingRepository(ctx);

        double.TryParse(settingsRepo.GetValue(AvgSecondsPerTemplateKey),
            System.Globalization.CultureInfo.InvariantCulture, out var prevAvg);
        int.TryParse(settingsRepo.GetValue(HistoryTemplateCountKey),
            System.Globalization.CultureInfo.InvariantCulture, out var prevCount);

        var thisRunAvg = result.TotalElapsedSeconds / result.SuccessCount;
        var newCount = prevCount + result.SuccessCount;
        var newAvg = prevCount <= 0
            ? thisRunAvg
            : ((prevAvg * prevCount) + (thisRunAvg * result.SuccessCount)) / newCount;

        settingsRepo.SetValue(AvgSecondsPerTemplateKey, newAvg.ToString(System.Globalization.CultureInfo.InvariantCulture));
        settingsRepo.SetValue(HistoryTemplateCountKey, newCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ctx.SaveChanges();
    }

    /// <summary>Formats a duration in seconds as a short, human-readable string.</summary>
    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s"
            : ts.TotalMinutes >= 1
                ? $"{ts.Minutes}m {ts.Seconds}s"
                : $"{ts.Seconds}s";
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

        TimingSummaryLabel.Text = result.SuccessCount > 0
            ? $"Total time: {FormatDuration(result.TotalElapsedSeconds)} " +
              $"(average {FormatDuration(result.TotalElapsedSeconds / result.SuccessCount)} per template)."
            : string.Empty;

        var (mean, stdDev, outliers) = TemplateRefreshService.FindSlowOutliers(result.Timings);
        if (outliers.Count > 0)
        {
            SlowDownloadsLabel.Text = $"{outliers.Count} download(s) took unusually long " +
                $"(more than 1 standard deviation above the {FormatDuration(mean)} average):";
            SlowDownloadsList.ItemsSource = outliers
                .Select(o => $"[ID {o.TemplateId}] {o.TemplateName}: {FormatDuration(o.ElapsedSeconds)} " +
                             $"(average {FormatDuration(mean)}, std dev {FormatDuration(stdDev)})")
                .ToList();
            SlowDownloadsPanel.IsVisible = true;

            TimingReportPathLabel.Text  = _timingReportPath != null ? $"Timing report saved to: {_timingReportPath}" : string.Empty;
            OpenTimingReportButton.IsVisible = _timingReportPath != null;
        }
        else
        {
            SlowDownloadsPanel.IsVisible = false;
        }

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

    /// <summary>
    /// Writes a timestamped text report listing every template whose download time was more than
    /// one standard deviation above the run's average, along with a likely reason, so unusually
    /// slow sources can be investigated. Returns null (and does nothing) if there are no outliers.
    /// Best-effort: a write failure is reported in <see cref="StatusLabel"/> rather than throwing.
    /// </summary>
    private string? WriteTimingReport(string saveLocation, RefreshResult result)
    {
        var (mean, stdDev, outliers) = TemplateRefreshService.FindSlowOutliers(result.Timings);
        if (outliers.Count == 0)
            return null;

        try
        {
            var reportPath = Path.Combine(saveLocation,
                $"TemplateRefreshTiming_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Template Refresh Timing Report \u2014 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total refresh time: {FormatDuration(result.TotalElapsedSeconds)} " +
                          $"for {result.SuccessCount} template(s).");
            sb.AppendLine($"Average download time: {FormatDuration(mean)}, standard deviation: {FormatDuration(stdDev)}.");
            sb.AppendLine($"{outliers.Count} download(s) exceeded {FormatDuration(mean + stdDev)} (mean + 1 std dev):");
            sb.AppendLine();

            foreach (var o in outliers)
            {
                var multiple = stdDev > 0 ? (o.ElapsedSeconds - mean) / stdDev : 0;
                sb.AppendLine($"[ID {o.TemplateId}] {o.TemplateName}");
                sb.AppendLine($"  Download time: {FormatDuration(o.ElapsedSeconds)} " +
                              $"({multiple:F1} std dev above the {FormatDuration(mean)} average)");
                sb.AppendLine($"  Likely reason: unusually slow or throttled response from the source server, " +
                              $"a larger-than-average file, or transient network latency during this run.");
                sb.AppendLine();
            }

            Directory.CreateDirectory(saveLocation);
            File.WriteAllText(reportPath, sb.ToString());
            return reportPath;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Refresh complete, but the timing report could not be written: {ex.Message}";
            return null;
        }
    }

    private void OpenTimingReport()
    {
        if (_timingReportPath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_timingReportPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not open the timing report: {ex.Message}";
        }
    }
}
