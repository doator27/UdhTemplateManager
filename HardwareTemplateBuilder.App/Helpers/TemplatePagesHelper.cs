using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Page handling for an "add template" form: when the Online Link or Local File changes, the
/// PDF's pages are counted and, if Pages To Print is empty (or still the value this helper filled
/// in), it becomes all pages ("1-N"); Num Pages always follows the Pages To Print range; and at
/// save time an empty Pages To Print means all pages (<see cref="EnsurePagesAsync"/>).
/// </summary>
public sealed class TemplatePagesHelper
{
    private readonly TextBox _online;
    private readonly TextBox _local;
    private readonly TextBox _numPages;
    private readonly TextBox _pages;
    private string? _autoPages;
    private string _lastSource = string.Empty;
    private CancellationTokenSource? _cts;

    /// <summary>Hooks the helper to a form's boxes.</summary>
    /// <param name="online">Online Link box.</param>
    /// <param name="local">Local File box.</param>
    /// <param name="numPages">Num Pages box.</param>
    /// <param name="pages">Pages To Print box.</param>
    public TemplatePagesHelper(TextBox online, TextBox local, TextBox numPages, TextBox pages)
    {
        _online = online;
        _local = local;
        _numPages = numPages;
        _pages = pages;
        _online.LostFocus += async (_, _) => await DetectAsync();
        _local.LostFocus += async (_, _) => await DetectAsync();
        _pages.TextChanged += (_, _) =>
        {
            if (TemplatePageInfo.CountInRange(_pages.Text) is int n) _numPages.Text = n.ToString();
        };
    }

    /// <summary>Forgets what was counted; call after loading an existing template or clearing the form.</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _autoPages = null;
        _lastSource = Source();
    }

    /// <summary>Counts the PDF's pages if the link/file changed since the last count.</summary>
    /// <param name="force">Count even if the link/file didn't change.</param>
    public async Task DetectAsync(bool force = false)
    {
        var source = Source();
        if (!force && source == _lastSource) return;
        _lastSource = source;
        if (source == "|") return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var count = await TemplatePageInfo.CountPagesAsync(_local.Text, _online.Text, cts.Token);
        if (cts.IsCancellationRequested || count is not int pages) return;
        ApplyCount(pages);
    }

    /// <summary>Uses a known page count: fills Pages To Print with all pages if it's empty (or auto-filled).</summary>
    /// <param name="pageCount">The PDF's page count.</param>
    public void ApplyCount(int pageCount)
    {
        if (pageCount < 1) return;
        var current = _pages.Text?.Trim() ?? string.Empty;
        if (current.Length == 0 || current == _autoPages)
        {
            _autoPages = TemplatePageInfo.AllPages(pageCount);
            _pages.Text = _autoPages;
        }
        if (TemplatePageInfo.CountInRange(_pages.Text) is int n) _numPages.Text = n.ToString();
    }

    /// <summary>
    /// The Pages To Print to save. When the box is empty, counts the PDF and uses all pages.
    /// Returns null when it's empty and the PDF can't be read to count its pages.
    /// </summary>
    public async Task<string?> EnsurePagesAsync()
    {
        var current = _pages.Text?.Trim();
        if (!string.IsNullOrEmpty(current)) return current;
        var count = await TemplatePageInfo.CountPagesAsync(_local.Text, _online.Text);
        if (count is not int pages) return null;
        ApplyCount(pages);
        return _pages.Text?.Trim();
    }

    private string Source() => $"{_local.Text?.Trim()}|{_online.Text?.Trim()}";
}
