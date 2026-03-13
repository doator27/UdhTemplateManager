using Avalonia.Controls;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>CRUD view for managing <see cref="Customer"/> records.</summary>
public partial class CustomersView : UserControl
{
    private CustomerRepository? _repo;
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view and loads data.</summary>
    public CustomersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new CustomerRepository(context);
        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this customer?"))
                DeleteSelected();
        };
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(c => string.IsNullOrEmpty(filter) || c.CustomerName.ToLower().Contains(filter))
            .OrderBy(c => c.CustomerName)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberBinding = new Avalonia.Data.Binding("CustomerName");
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is Customer c)
        {
            _selectedId = c.Id;
            CustomerNameBox.Text = c.CustomerName;
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var name = CustomerNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Customer Name is required."; return; }

        if (_selectedId == 0)
        {
            _repo!.Add(new Customer { CustomerName = name });
            StatusLabel.Text = "Saved.";
        }
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null) { existing.CustomerName = name; _repo.Update(existing); }
            StatusLabel.Text = "Updated.";
        }
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
        CustomerNameBox.Text = "";
        StatusLabel.Text = "";
        RecordList.SelectedItem = null;
    }
}
