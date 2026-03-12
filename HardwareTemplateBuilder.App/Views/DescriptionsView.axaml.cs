using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="Description"/> records.</summary>
public partial class DescriptionsView : UserControl
{
    private DescriptionRepository? _repo;
    private int _selectedId;

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
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
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
            .OrderBy(d => d.DescriptionText)
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
            _repo!.Add(new Description { DescriptionText = text });
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null) { existing.DescriptionText = text; _repo.Update(existing); }
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
        DescriptionBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
