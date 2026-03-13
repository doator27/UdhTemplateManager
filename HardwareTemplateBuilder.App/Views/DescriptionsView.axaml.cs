using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="Description"/> records, including manual sort-order reordering.</summary>
public partial class DescriptionsView : UserControl
{
    private DescriptionRepository? _repo;
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view and loads data.</summary>
    public DescriptionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new DescriptionRepository(context);
        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        MoveUpButton.Click += (_, _) => MoveSelected(-1);
        MoveDownButton.Click += (_, _) => MoveSelected(1);
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this description?"))
                DeleteSelected();
        };
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(d => string.IsNullOrEmpty(filter) || d.DescriptionText.ToLower().Contains(filter))
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is Description d)
        {
            _selectedId = d.Id;
            DescriptionBox.Text = d.DescriptionText;
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var text = DescriptionBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { StatusLabel.Text = "Description is required."; return; }

        if (_selectedId == 0)
        {
            var maxOrder = _repo!.GetAll().Any() ? _repo.GetAll().Max(d => d.SortOrder) : -1;
            _repo!.Add(new Description { DescriptionText = text, SortOrder = maxOrder + 1 });
        }
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null)
            {
                existing.DescriptionText = text;
                _repo.Update(existing);
            }
        }
        StatusLabel.Text = "Saved.";
        LoadList();
    }

    /// <summary>
    /// Moves the currently selected description up (<paramref name="direction"/> = -1) or
    /// down (+1) by swapping its <see cref="Description.SortOrder"/> with its neighbour.
    /// </summary>
    private void MoveSelected(int direction)
    {
        if (_selectedId == 0) return;

        // Build the current ordered list (apply same filter so Up/Down respect what's visible).
        var filter = FilterBox.Text?.ToLower() ?? "";
        var ordered = _repo!.GetAll()
            .Where(d => string.IsNullOrEmpty(filter) || d.DescriptionText.ToLower().Contains(filter))
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText)
            .ToList();

        // Normalise SortOrder to 0,1,2,... so swaps are always unambiguous.
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;

        var idx = ordered.FindIndex(d => d.Id == _selectedId);
        if (idx < 0) return;

        var swapIdx = idx + direction;
        if (swapIdx < 0 || swapIdx >= ordered.Count) return;

        // Swap the two SortOrder values.
        (ordered[idx].SortOrder, ordered[swapIdx].SortOrder) = (ordered[swapIdx].SortOrder, ordered[idx].SortOrder);

        // Persist all changed items.
        using var context = DatabaseInitializer.CreateContext();
        foreach (var d in ordered)
        {
            var entity = context.Descriptions.Find(d.Id);
            if (entity != null)
            {
                entity.SortOrder = d.SortOrder;
            }
        }
        context.SaveChanges();

        LoadList();

        // Reselect the moved item.
        var newList = RecordList.ItemsSource as List<Description>;
        if (newList != null)
        {
            var moved = newList.FirstOrDefault(d => d.Id == _selectedId);
            if (moved != null) RecordList.SelectedItem = moved;
        }
    }

    private void DeleteSelected()
    {
        if (_selectedId == 0) return;
        _repo!.Delete(_selectedId);
        ClearForm();
        LoadList();
    }

    private void ClearForm()
    {
        _selectedId = 0;
        DescriptionBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
