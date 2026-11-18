using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services;

using HardwareTemplateBuilder.Core.Services.TemplateFinder;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// "Templates to Fix": the hardware items whose template failed in the last full template
/// refresh (see <see cref="TemplateFixListService"/>), laid out like the bulk-add item page.
/// Each card's "Add/Edit Template" opens the item's detail page; once the item has a working
/// template it drops off this list the next time the page loads (e.g. when you come Back).
/// </summary>
public partial class TemplateFixListView : UserControl
{
    /// <summary>The route name this view is registered under (also the detail page's return route).</summary>
    public const string Route = "TemplateFixList";

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public TemplateFixListView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        BackButton.Click += (_, _) => NavigationRequested?.Invoke("RefreshTemplates");
        ReloadButton.Click += (_, _) => LoadItems();
        LoadItems();
    }

    private void LoadItems()
    {
        ItemsPanel.Children.Clear();
        StatusLabel.Text = string.Empty;

        List<TemplateFixItem> items;
        DateTime? createdUtc;
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            items = TemplateFixListService.GetOutstanding(ctx);
            createdUtc = TemplateFixListService.CreatedUtc(ctx);
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = AppColors.Danger;
            StatusLabel.Text = $"Could not load the list: {ex.Message}";
            return;
        }

        var when = createdUtc.HasValue ? $" ({createdUtc.Value.ToLocalTime():yyyy-MM-dd h:mm tt})" : string.Empty;
        IntroLabel.Text =
            $"Hardware items whose template failed in the last template refresh{when}. " +
            "Use Add/Edit Template to give each one a working template (add a new one, link an existing one, " +
            "or fix the broken template's link); it comes off this list once it has one.";

        if (items.Count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = createdUtc.HasValue
                    ? "Nothing left to fix. Every item from the last refresh has a working template."
                    : "No refresh failures recorded yet. Run Maintenance > Refresh Templates first.",
                Foreground = AppColors.Success,
                Margin = new Avalonia.Thickness(4),
            });
            return;
        }

        StatusLabel.Foreground = AppColors.Muted;
        StatusLabel.Text = $"{items.Count} item(s) to fix.";

        foreach (var group in items.GroupBy(i => i.ManufacturerName))
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(group.Key) ? "(no manufacturer)" : group.Key,
                FontWeight = FontWeight.Bold,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });
            foreach (var item in group)
                ItemsPanel.Children.Add(BuildCard(item));
        }
    }

    private Border BuildCard(TemplateFixItem item)
    {
        var descText = new TextBlock
        {
            Text = item.Description,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var modelText = new TextBlock
        {
            Text = item.ModelNumber,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var problemText = new TextBlock
        {
            Text = string.Join(Environment.NewLine, item.FailedTemplates.Select(t => $"Template {t.TemplateNumber}: {t.Error}")),
            Foreground = AppColors.Danger,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var editButton = new Button
        {
            Content = "Add/Edit Template",
            Margin = new Avalonia.Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        editButton.Click += (_, _) =>
            NavigationRequested?.Invoke($"HardwareItemDetail:{item.HardwareItemId}:{Route}");

        var dismissButton = new Button
        {
            Content = "Dismiss",
            VerticalAlignment = VerticalAlignment.Center,
        };
        dismissButton.Click += (_, _) =>
        {
            using var ctx = DatabaseInitializer.CreateContext();
            TemplateFixListService.Dismiss(ctx, item.HardwareItemId);
            LoadItems();
        };

        // One "Find New Link" per failed template.
        var findPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(4, 0, 4, 0) };
        foreach (var failed in item.FailedTemplates)
        {
            var findButton = new Button
            {
                Content = item.FailedTemplates.Count == 1 ? "Find New Link..." : $"Find Link: {failed.TemplateNumber}",
                Margin = new Avalonia.Thickness(0, 1, 0, 1),
            };
            var templateId = failed.TemplateId;
            findButton.Click += async (_, _) =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window != null && await TemplateFinderFlow.FixBrokenTemplateAsync(window, templateId)) LoadItems();
            };
            findPanel.Children.Add(findButton);
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,150,*,Auto,Auto,Auto") };
        Grid.SetColumn(descText, 0); row.Children.Add(descText);
        Grid.SetColumn(modelText, 1); row.Children.Add(modelText);
        Grid.SetColumn(problemText, 2); row.Children.Add(problemText);
        row.Children.Add(editButton);
        Grid.SetColumn(findPanel, 3); row.Children.Add(findPanel);
        Grid.SetColumn(editButton, 4);
        Grid.SetColumn(dismissButton, 5); row.Children.Add(dismissButton);

        return new Border
        {
            BorderBrush = AppColors.Muted,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(2),
            Padding = new Avalonia.Thickness(10, 8),
            Child = row,
        };
    }
}
