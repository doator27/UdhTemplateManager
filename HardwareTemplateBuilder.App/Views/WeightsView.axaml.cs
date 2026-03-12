using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="Weight"/> records.</summary>
public partial class WeightsView : UserControl
{
    private WeightRepository? _repo;
    private List<Description> _descriptions = new();
    private readonly WeightParser _weightParser = new();
    private int _selectedId;

    /// <summary>Initializes the view.</summary>
    public WeightsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new WeightRepository(context);
        _descriptions = new DescriptionRepository(context).GetAll().OrderBy(d => d.DescriptionText).ToList();
        DescriptionCombo.ItemsSource = _descriptions;
        DescriptionCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");

        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this weight?"))
                DeleteSelected();
        };
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(w => string.IsNullOrEmpty(filter) || w.WeightValue.ToLower().Contains(filter))
            .OrderBy(w => w.WeightValue)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("WeightValue");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is Weight w)
        {
            _selectedId = w.Id;
            WeightValueBox.Text = w.WeightValue;
            DescriptionCombo.SelectedItem = _descriptions.FirstOrDefault(d => d.Id == w.DescriptionId);
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var value = WeightValueBox.Text?.Trim();
        if (string.IsNullOrEmpty(value)) { StatusLabel.Text = "Weight value is required."; return; }

        try { _weightParser.Parse(value); }
        catch { StatusLabel.Text = "Invalid format. Use 00.000.000 (e.g. 01.002.015)."; return; }

        if (DescriptionCombo.SelectedItem is not Description desc)
        { StatusLabel.Text = "Please select a description."; return; }

        if (_selectedId == 0)
            _repo!.Add(new Weight { WeightValue = value, DescriptionId = desc.Id });
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null) { existing.WeightValue = value; existing.DescriptionId = desc.Id; _repo.Update(existing); }
        }
        StatusLabel.Text = "Saved.";
        LoadList();
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
        WeightValueBox.Text = "";
        DescriptionCombo.SelectedItem = null;
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
