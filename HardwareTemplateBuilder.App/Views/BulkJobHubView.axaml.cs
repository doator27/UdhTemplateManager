using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Hub view showing each selected manufacturer with an item count and an Edit button.
/// Navigates between <see cref="BulkManufacturerSelectionView"/> and
/// <see cref="BulkHardwareEntryView"/> (per-manufacturer mode).
/// </summary>
public partial class BulkJobHubView : UserControl
{
    private readonly int _jobId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the hub for the given job.</summary>
    public BulkJobHubView(int jobId)
    {
        _jobId = jobId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_jobId);
        HeaderLabel.Text = job != null
            ? $"Bulk Add — {job.JobNumber}"
            : $"Bulk Add — Job #{_jobId}";

        // Reload selected manufacturers from draft (in case user navigated back and changed them).
        var draft = BulkDraftService.Load(_jobId);
        if (draft.ManufacturerIds.Count == 0)
        {
            StatusLabel.Text = "No manufacturers selected. Go back and select manufacturers first.";
            ContinueButton.IsEnabled = false;
        }

        var mfrLookup = new ManufacturerRepository(ctx).GetAll().ToDictionary(m => m.Id);
        var mfrs = draft.ManufacturerIds
            .Select(id => mfrLookup.TryGetValue(id, out var m) ? m : null)
            .Where(m => m != null)
            .Cast<Manufacturer>()
            .ToList();

        BulkAddSession.SelectedManufacturers = mfrs;
        BulkAddSession.JobId                = _jobId;

        BuildCards(mfrs, draft);

        BackButton.Click     += (_, _) => NavigationRequested?.Invoke($"BulkManufacturerSelection:{_jobId}");
        ContinueButton.Click += (_, _) => OnContinue(mfrs, draft);
    }

    private void BuildCards(List<Manufacturer> manufacturers, BulkDraftV2 draft)
    {
        CardsPanel.Children.Clear();

        foreach (var mfr in manufacturers)
        {
            var itemCount = draft.Items.Count(i => i.ManufacturerId == mfr.Id
                && !string.IsNullOrWhiteSpace(i.ModelNumber));

            var countLabel = new TextBlock
            {
                Text = itemCount == 0 ? "No items yet" : $"{itemCount} item{(itemCount == 1 ? "" : "s")}",
                Foreground = itemCount == 0 ? Brushes.DarkOrange : Brushes.DarkGreen,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0)
            };

            var editBtn = new Button
            {
                Content = "Edit Items →",
                Padding = new Avalonia.Thickness(8, 4)
            };
            var capturedId = mfr.Id;
            editBtn.Click += (_, _) =>
                NavigationRequested?.Invoke($"BulkHardwareEntry:{_jobId}:{capturedId}");

            var nameLabel = new TextBlock
            {
                Text              = mfr.ManufacturerName,
                FontWeight        = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,Auto") };
            Grid.SetColumn(nameLabel,   0); row.Children.Add(nameLabel);
            Grid.SetColumn(countLabel,  1); row.Children.Add(countLabel);
            Grid.SetColumn(editBtn,     2); row.Children.Add(editBtn);

            var card = new Border
            {
                BorderBrush     = Brushes.Gray,
                BorderThickness = new Avalonia.Thickness(1),
                Padding         = new Avalonia.Thickness(10, 8),
                Child           = row
            };
            CardsPanel.Children.Add(card);
        }
    }

    private void OnContinue(List<Manufacturer> manufacturers, BulkDraftV2 draft)
    {
        // Build PendingRows from draft items, grouped by manufacturer order.
        var rows = new List<BulkHardwareRow>();

        using var ctx = DatabaseInitializer.CreateContext();
        var mfrLookup  = new ManufacturerRepository(ctx).GetAll().ToDictionary(m => m.Id);
        var descLookup = new DescriptionRepository(ctx).GetAll().ToDictionary(d => d.Id);

        foreach (var mfr in manufacturers)
        {
            var mfrItems = draft.Items.Where(i => i.ManufacturerId == mfr.Id
                && !string.IsNullOrWhiteSpace(i.ModelNumber));

            foreach (var item in mfrItems)
            {
                Description? desc = item.DescriptionId.HasValue &&
                                    descLookup.TryGetValue(item.DescriptionId.Value, out var d) ? d : null;

                // Match against existing HardwareItem.
                HardwareItem? matched = null;
                if (desc != null)
                {
                    matched = ctx.HardwareItems.FirstOrDefault(h =>
                        h.ManufacturerId == mfr.Id &&
                        h.DescriptionId  == desc.Id &&
                        h.ModelNumber    == item.ModelNumber);
                }

                var row = new BulkHardwareRow
                {
                    SelectedManufacturer = mfr,
                    SelectedDescription  = desc,
                    ModelNumber          = item.ModelNumber,
                    MatchedItem          = matched,
                    Labels = item.Labels.Select(l => new BulkHardwareLabel
                    {
                        CustomLabel = l.CustomLabel,
                        Remarks     = l.Remarks
                    }).ToList()
                };

                // Ensure at least one (blank) label so the wizard always has something to save.
                if (row.Labels.Count == 0)
                    row.Labels.Add(new BulkHardwareLabel());

                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            StatusLabel.Text = "Add at least one item before continuing.";
            return;
        }

        BulkAddSession.PendingRows = rows;
        NavigationRequested?.Invoke($"TemplateResolutionWizard:{_jobId}");
    }
}
