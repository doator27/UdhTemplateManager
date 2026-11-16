using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Ui;

namespace HardwareTemplateBuilder.Wpf.Views;

/// <summary>CRUD view for managing <see cref="Manufacturer"/> records.</summary>
public partial class ManufacturersView : UserControl
{
    private ManufacturerRepository? _repo;
    private IHardwareItemRepository? _hardwareItemRepo;
    private int _selectedId;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event System.Action<string>? NavigationRequested;

    /// <summary>Initializes the view and loads data.</summary>
    public ManufacturersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new ManufacturerRepository(context);
        _hardwareItemRepo = new HardwareItemRepository(context);
        LoadList();
        FilterBox.TextChanged += (_, _) => LoadList();
        RecordList.SelectionChanged += (_, _) => OnSelectionChanged();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = this.OwnerWindow();
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this manufacturer?"))
                DeleteSelected();
        };
        CopyItemsButton.Click += async (_, _) => await CopyItemsToOtherManufacturerAsync();
    }

    private void LoadList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(m => string.IsNullOrEmpty(filter) || m.ManufacturerName.ToLower().Contains(filter))
            .OrderBy(m => m.ManufacturerName)
            .ToList();
        RecordList.ItemsSource = items;
        RecordList.DisplayMemberPath = "ManufacturerName";
    }

    private void OnSelectionChanged()
    {
        if (RecordList.SelectedItem is Manufacturer m)
        {
            _selectedId = m.Id;
            NameBox.Text = m.ManufacturerName;
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Manufacturer Name is required."; return; }
        if (_selectedId == 0)
            _repo!.Add(new Manufacturer { ManufacturerName = name });
        else
        {
            var existing = _repo!.GetById(_selectedId);
            if (existing != null) { existing.ManufacturerName = name; _repo.Update(existing); }
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

    private async System.Threading.Tasks.Task CopyItemsToOtherManufacturerAsync()
    {
        var window = this.OwnerWindow();
        if (window == null) return;

        if (_selectedId == 0)
        {
            await DialogHelper.ShowInfoAsync(window, "Select a manufacturer first.");
            return;
        }

        var source = _repo!.GetById(_selectedId);
        if (source == null) return;

        var otherManufacturers = _repo.GetAll()
            .Where(m => m.Id != _selectedId)
            .OrderBy(m => m.ManufacturerName)
            .ToList();

        if (otherManufacturers.Count == 0)
        {
            await DialogHelper.ShowInfoAsync(window, "There are no other manufacturers to copy items to.");
            return;
        }

        var target = await PickManufacturerAsync(window, source.ManufacturerName, otherManufacturers);
        if (target == null) return;

        var copiedCount = _hardwareItemRepo!.CopyItemsToManufacturer(source.Id, target.Id);
        await DialogHelper.ShowInfoAsync(
            window,
            $"Copied {copiedCount} item(s) from \"{source.ManufacturerName}\" to \"{target.ManufacturerName}\". " +
            "The original items remain under both manufacturers.",
            "Copy Complete");
    }

    private static async System.Threading.Tasks.Task<Manufacturer?> PickManufacturerAsync(Window owner, string sourceName, System.Collections.Generic.List<Manufacturer> options)
    {
        var dialog = new Win95Window
        {
            Title = "Copy Items To...",
            Width = 360,
            Height = 170,
            ResizeMode = ResizeMode.NoResize,
        };

        Manufacturer? selected = options[0];
        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = "ManufacturerName",
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 12),
        };
        combo.SelectionChanged += (_, _) => selected = combo.SelectedItem as Manufacturer;

        var okButton = new Button { Content = "Copy", Width = 80, Margin = new Thickness(4), IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(4), IsCancel = true };
        var confirmed = false;
        okButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => { confirmed = false; dialog.Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(new TextBlock { Text = $"Copy hardware items from \"{sourceName}\" to:", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(combo);
        body.Children.Add(buttons);
        dialog.Content = body;

        await dialog.ShowDialogAsync(owner);
        return confirmed ? selected : null;
    }
}
