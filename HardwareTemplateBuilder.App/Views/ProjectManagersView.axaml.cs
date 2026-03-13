using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="ProjectManager"/> records.</summary>
public partial class ProjectManagersView : UserControl
{
    private ProjectManagerRepository? _repo;
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view.</summary>
    public ProjectManagersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new ProjectManagerRepository(context);
        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this project manager?"))
                DeleteSelected();
        };
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(pm => string.IsNullOrEmpty(filter) || pm.ProjectManagerName.ToLower().Contains(filter))
            .OrderBy(pm => pm.ProjectManagerName)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("ProjectManagerName");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is ProjectManager pm)
        {
            _selectedId = pm.Id;
            NameBox.Text = pm.ProjectManagerName;
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Name is required."; return; }
        if (_selectedId == 0)
            _repo!.Add(new ProjectManager { ProjectManagerName = name });
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null) { existing.ProjectManagerName = name; _repo.Update(existing); }
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
        NameBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
