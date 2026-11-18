using System;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services.TemplateFinder;

/// <summary>
/// Gives a template a new online link (e.g. one found by the template finder for a broken link)
/// and downloads it again into the template storage location, the same way Refresh Templates
/// does. Changing the link also takes the template's items off the "Templates to Fix" list.
/// </summary>
public static class TemplateLinkReplacer
{
    /// <summary>Saves the new link and re-downloads the template.</summary>
    /// <param name="templateId">The template.</param>
    /// <param name="newLink">The new online link.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>True when the file was downloaded; false when the link was saved but the download failed.</returns>
    public static async Task<bool> ReplaceAsync(int templateId, string newLink, CancellationToken cancellationToken)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var template = await ctx.IndividualTemplates.FindAsync(new object[] { templateId }, cancellationToken)
                       ?? throw new InvalidOperationException("That template no longer exists.");
        template.OnlineLink = newLink.Trim();
        await ctx.SaveChangesAsync(cancellationToken);

        var configured = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
        var saveLocation = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        using var http = TemplateFinderService.CreateHttpClient();
        var refresh = new TemplateRefreshService(ctx, http);
        try
        {
            return await refresh.RefreshSingleAsync(templateId, saveLocation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
