using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>
/// "Find Template Online": searches for template PDFs for a hardware item or a broken template
/// (<see cref="TemplateFinderService"/>: your past link patterns, the manufacturer's website, saved
/// copies of broken links), lists the real PDFs found best first with a preview, lets you check a
/// link pasted from your browser, and opens a Google search in the browser. Returns the chosen
/// <see cref="TemplateCandidate"/>, or null when cancelled; the caller fills in its editor (or asks
/// before replacing a broken link).
/// </summary>
public partial class TemplateFinderWindow : Win95Window
{
    private static readonly HttpClient Http = TemplateFinderService.CreateHttpClient();

    private readonly TemplateSearchRequest _request;
    private readonly TemplateFinderService _service = new(Http, DatabaseInitializer.CreateContext);
    private readonly ObservableCollection<TemplateCandidate> _candidates = new();
    private CancellationTokenSource? _cts;
    private List<string> _sites = new();

    /// <summary>Parameterless constructor for the XAML designer.</summary>
    public TemplateFinderWindow() : this(new TemplateSearchRequest()) { }

    /// <summary>Creates the window for a search.</summary>
    /// <param name="request">What to look for.</param>
    public TemplateFinderWindow(TemplateSearchRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = $"Find Template Online - {request.Summary}";
        HeaderText.Text = $"Find Template Online: {request.Summary}";
        var terms = request.Terms();
        InfoText.Text =
            $"Looking for: {(terms.Count > 0 ? string.Join(", ", terms) : "(no model or template number)")}" +
            (request.Description != null ? $"   Description: {request.Description}" : string.Empty) +
            (request.BrokenLink != null ? $"\nOld link: {request.BrokenLink}" : string.Empty) +
            "\nSearches your past links for this manufacturer and the manufacturer's website. Every result is downloaded and checked.";

        CandidatesList.ItemsSource = _candidates;
        Loaded += async (_, _) => await SearchAsync();
        Closed += (_, _) => _cts?.Cancel();

        SearchButton.Click += async (_, _) => await SearchAsync();
        StopButton.Click += (_, _) => _cts?.Cancel();
        GoogleButton.Click += (_, _) => OpenInBrowser(_request.GoogleSearchUrl());
        SiteGoogleButton.Click += (_, _) => { if (_sites.Count > 0) OpenInBrowser(_request.GoogleSiteSearchUrl(_sites[0])); };
        CheckLinkButton.Click += async (_, _) => await CheckPastedLinkAsync();
        OpenLinkButton.Click += (_, _) => { if (CandidatesList.SelectedItem is TemplateCandidate c) OpenInBrowser(c.Url); };
        UseButton.Click += (_, _) => { if (CandidatesList.SelectedItem is TemplateCandidate { IsPdf: true } c) CloseWith(c); };
        CancelButton.Click += (_, _) => CloseWith(null);
        CandidatesList.SelectionChanged += async (_, e) =>
        {
            e.Handled = true;
            await ShowSelectedAsync();
        };
    }

    private async Task SearchAsync()
    {
        if (_sites.Count == 0)
        {
            try { _sites = await Task.Run(() => _service.SitesFor(_request)); } catch { /* no history */ }
            SiteGoogleButton.IsEnabled = _sites.Count > 0;
            SiteGoogleButton.Content = _sites.Count > 0 ? $"Search {_sites[0]} on Google" : "Search Mfr Site on Google";
        }
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        SearchButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusLabel.Foreground = AppColors.Muted;
        var progress = new Progress<string>(s => { if (!cts.IsCancellationRequested) StatusLabel.Text = s; });
        try
        {
            await Task.Run(() => _service.FindAsync(_request, progress, c => Dispatcher.InvokeAsync(() => Add(c)), cts.Token));
            StatusLabel.Text = _candidates.Any(c => c.IsPdf)
                ? $"Found {_candidates.Count(c => c.IsPdf)} PDF(s). Check the preview, then Use This Link."
                : "No template PDFs found. Try Search Google in Browser, copy the PDF's link and paste it above.";
            var blocked = _service.BlockedSites;
            if (blocked.Count > 0)
                StatusLabel.Text += $"\n{string.Join(", ", blocked)} blocked the automated search (bot protection), so search {(blocked.Count == 1 ? "it" : "them")} on Google instead and paste the PDF's link above.";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Search stopped.";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (_cts == cts)
            {
                SearchButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }
    }

    private void Add(TemplateCandidate candidate)
    {
        var existing = _candidates.FirstOrDefault(c => string.Equals(c.Url, candidate.Url, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _candidates.Remove(existing);
        int index = 0;
        while (index < _candidates.Count && _candidates[index].Score >= candidate.Score) index++;
        _candidates.Insert(index, candidate);
        if (CandidatesList.SelectedItem == null && candidate.IsPdf) CandidatesList.SelectedItem = candidate;
    }

    private async Task CheckPastedLinkAsync()
    {
        var url = PasteBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = "Paste a full link starting with https://";
            return;
        }
        CheckLinkButton.IsEnabled = false;
        StatusLabel.Foreground = AppColors.Muted;
        StatusLabel.Text = "Checking the link...";
        try
        {
            var candidate = await Task.Run(() => _service.CheckUrlAsync(url, _request, CancellationToken.None));
            candidate.Score += 10000; // the user's own pick goes to the top
            Add(candidate);
            CandidatesList.SelectedItem = candidate;
            StatusLabel.Foreground = candidate.IsPdf ? AppColors.Success : AppColors.Danger;
            StatusLabel.Text = candidate.IsPdf
                ? $"That link is a {candidate.PageCount}-page PDF{(candidate.MentionsTerm ? $" that mentions {candidate.MatchedTerm}" : string.Empty)}."
                : $"That link isn't a usable PDF ({candidate.Error}). Open the PDF itself in your browser and copy its address.";
        }
        finally
        {
            CheckLinkButton.IsEnabled = true;
        }
    }

    private async Task ShowSelectedAsync()
    {
        var c = CandidatesList.SelectedItem as TemplateCandidate;
        SelectedUrlBox.Text = c?.Url ?? string.Empty;
        OpenLinkButton.IsEnabled = c != null;
        UseButton.IsEnabled = c?.IsPdf == true;
        await PreviewPanel.ShowPdfAsync(c?.PdfBytes);
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Couldn't open the browser: {ex.Message}";
        }
    }
}
