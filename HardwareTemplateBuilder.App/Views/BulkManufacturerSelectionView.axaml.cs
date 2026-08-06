using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// First step of the bulk-add workflow. Lets the user pick which manufacturers are
/// involved in this job before entering items per manufacturer.
/// </summary>
public partial class BulkManufacturerSelectionView : UserControl
{
    private readonly int _jobId;
    private List<Manufacturer> _allManufacturers = new();
    private readonly ObservableCollection<Manufacturer> _filtered   = new();
    private readonly ObservableCollection<Manufacturer> _selected   = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view for the given job.</summary>
    public BulkManufacturerSelectionView(int jobId)
    {
        _jobId = jobId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _allManufacturers = new ManufacturerRepository(ctx).GetAll()
            .OrderBy(m => m.ManufacturerName).ToList();

        // Restore manufacturer selection from draft.
        var draft = BulkDraftService.Load(_jobId);
        var selectedIds = new HashSet<int>(draft.ManufacturerIds);

        foreach (var m in _allManufacturers)
            if (selectedIds.Contains(m.Id)) AddSorted(m);

        AllMfrList.ItemsSource      = _filtered;
        AllMfrList.DisplayMemberBinding      = new Avalonia.Data.Binding("ManufacturerName");
        SelectedMfrList.ItemsSource = _selected;
        SelectedMfrList.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        ApplySearch();
        RefreshContinue();

        BackButton.Click     += (_, _) => GoBack();
        ContinueButton.Click += (_, _) => OnContinue();
        AddButton.Click      += (_, _) => AddSelected();
        RemoveButton.Click   += (_, _) => RemoveSelected();
        CreateMfrButton.Click += (_, _) => CreateManufacturer();

        AllMfrList.DoubleTapped += (_, _) => AddSelected();
        SelectedMfrList.DoubleTapped += (_, _) => RemoveSelected();

        SearchBox.TextChanged += (_, _) => ApplySearch();
    }

    private void ApplySearch()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        _filtered.Clear();
        var selectedIds = new HashSet<int>(_selected.Select(m => m.Id));
        var matches = string.IsNullOrEmpty(q)
            ? _allManufacturers
            : _allManufacturers.Where(m =>
                m.ManufacturerName.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var m in matches.Where(m => !selectedIds.Contains(m.Id)))
            _filtered.Add(m);
    }

    private void AddSelected()
    {
        var items = AllMfrList.SelectedItems?.Cast<Manufacturer>().ToList() ?? new List<Manufacturer>();
        if (items.Count == 0) return;

        foreach (var m in items)
        {
            if (_selected.Any(s => s.Id == m.Id)) continue;
            AddSorted(m);
            _filtered.Remove(m);
        }
        RefreshContinue();
        SaveDraft();
    }

    private void RemoveSelected()
    {
        var items = SelectedMfrList.SelectedItems?.Cast<Manufacturer>().ToList() ?? new List<Manufacturer>();
        if (items.Count == 0) return;

        foreach (var m in items)
            _selected.Remove(m);
        ApplySearch();     // re-adds to filtered list
        RefreshContinue();
        SaveDraft();
    }

    private void CreateManufacturer()
    {
        CreateStatusLabel.Text = string.Empty;
        var name = NewMfrNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { CreateStatusLabel.Text = "Enter a name first."; return; }

        using var ctx = DatabaseInitializer.CreateContext();
        var created = new ManufacturerRepository(ctx).Add(new Manufacturer { ManufacturerName = name });

        // Update the local list and add to selection immediately.
        _allManufacturers = new ManufacturerRepository(ctx).GetAll()
            .OrderBy(m => m.ManufacturerName).ToList();

        if (!_selected.Any(s => s.Id == created.Id))
        {
            AddSorted(created);
            RefreshContinue();
            SaveDraft();
        }

        NewMfrNameBox.Text = string.Empty;
        ApplySearch();
    }

    private void AddSorted(Manufacturer m)
    {
        var index = 0;
        while (index < _selected.Count &&
               string.Compare(_selected[index].ManufacturerName, m.ManufacturerName, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        _selected.Insert(index, m);
    }

    private void RefreshContinue()
    {
        SelectedCountLabel.Text  = $"{_selected.Count} selected";
        ContinueButton.IsEnabled = _selected.Count > 0;
    }

    private void SaveDraft()
    {
        var draft = BulkDraftService.Load(_jobId);
        draft.ManufacturerIds = _selected.Select(m => m.Id).ToList();
        BulkDraftService.Save(_jobId, draft);
    }

    private void GoBack()
    {
        SaveDraft();
        NavigationRequested?.Invoke($"JobDetail:{_jobId}");
    }

    private void OnContinue()
    {
        if (_selected.Count == 0) return;
        SaveDraft();

        BulkAddSession.JobId                = _jobId;
        BulkAddSession.SelectedManufacturers = _selected.ToList();
        NavigationRequested?.Invoke($"BulkJobHub:{_jobId}");
    }
}
