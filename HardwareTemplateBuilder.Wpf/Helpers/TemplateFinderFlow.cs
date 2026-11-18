using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HardwareTemplateBuilder.Wpf.Views;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;

namespace HardwareTemplateBuilder.Wpf.Helpers;

/// <summary>
/// The two ways screens use the "Find Template Online" window: find a PDF and hand it back so the
/// caller can fill in its template editor for the user to confirm, or repair a broken template
/// (find a new link, confirm, save it and download it again).
/// </summary>
public static class TemplateFinderFlow
{
    /// <summary>Opens the finder; returns the chosen PDF, or null when cancelled.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="request">What to look for.</param>
    public static async Task<TemplateCandidate?> FindAsync(Window owner, TemplateSearchRequest request) =>
        await new TemplateFinderWindow(request).ShowDialogAsync<TemplateCandidate>(owner);

    /// <summary>Opens the finder for an existing hardware item.</summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="hardwareItemId">The item.</param>
    public static async Task<TemplateCandidate?> FindForItemAsync(Window owner, int hardwareItemId)
    {
        TemplateSearchRequest request;
        using (var ctx = DatabaseInitializer.CreateContext())
            request = TemplateSearchRequest.ForHardwareItem(ctx, hardwareItemId);
        return await FindAsync(owner, request);
    }

    /// <summary>
    /// Finds a new link for a template whose link is broken, asks before replacing it, then saves
    /// it and downloads the template again. Returns true when the link was replaced.
    /// </summary>
    /// <param name="owner">Owner window.</param>
    /// <param name="templateId">The broken template.</param>
    public static async Task<bool> FixBrokenTemplateAsync(Window owner, int templateId)
    {
        TemplateSearchRequest request;
        using (var ctx = DatabaseInitializer.CreateContext())
            request = TemplateSearchRequest.ForTemplate(ctx, templateId);

        var found = await FindAsync(owner, request);
        if (found == null) return false;

        bool replace = await DialogHelper.ConfirmAsync(owner,
            $"Replace the link for template {request.TemplateNumber} ({request.ManufacturerName})?\n\n" +
            $"Old: {request.BrokenLink ?? "(none)"}\nNew: {found.Url}\n\n" +
            "The template will be downloaded again from the new link.",
            "Replace Template Link");
        if (!replace) return false;

        bool downloaded = await TemplateLinkReplacer.ReplaceAsync(templateId, found.Url, CancellationToken.None);
        await DialogHelper.ShowInfoAsync(owner,
            downloaded
                ? $"Template {request.TemplateNumber} now uses the new link and was downloaded again."
                : $"The new link was saved for template {request.TemplateNumber}, but downloading it failed. Run Refresh Templates later, or check the link.",
            "Replace Template Link");
        return true;
    }
}
