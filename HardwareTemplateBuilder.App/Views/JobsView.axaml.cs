using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// CRUD view for <see cref="Job"/> records, including a linked-hardware sub-panel
/// for managing <see cref="JobHardware"/> junction records with drag-and-drop reordering.
/// </summary>
public partial class JobsView : UserControl
{
    private JobRepository? _repo;
    private JobHardwareRepository? _jobHardwareRepo;
    private List<Customer> _customers = new();
    private List<ProjectManager> _projectManagers = new();
    private List<UserProfile> _userProfiles = new();
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _descriptions = new();

    /// <summary>The observable collection backing the linked hardware listbox, enabling drag-and-drop reorder.</summary>
    private ObservableCollection<HardwareItem> _linkedHardware = new();

    private int _selectedJobId;

    /// <summary>The hardware item being dragged for reorder operations.</summary>
    private HardwareItem? _draggedItem;

    private bool _isDragging;
    private global::Avalonia.Point _dragStartPoint;

    /// <summary>Initializes the view.</summary>
    public JobsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        var context = DatabaseInitializer.CreateContext();
        _repo = new JobRepository(context);
        _jobHardwareRepo = new JobHardwareRepository(context);

        _customers = new CustomerRepository(context).GetAll().OrderBy(c => c.CustomerName).ToList();
        _projectManagers = new ProjectManagerRepository(context).GetAll().OrderBy(pm => pm.ProjectManagerName).ToList();
        _userProfiles = new UserProfileRepository(context).GetAll().OrderBy(u => u.UserName).ToList();
        _manufacturers = new ManufacturerRepository(context).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _descriptions = new DescriptionRepository(context).GetAll().OrderBy(d => d.DescriptionText).ToList();

        CustomerCombo.ItemsSource = _customers;
        CustomerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("CustomerName");
        ProjectManagerCombo.ItemsSource = _projectManagers;
        ProjectManagerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ProjectManagerName");
        UserProfileCombo.ItemsSource = _userProfiles;
        UserProfileCombo.DisplayMemberBinding = new Avalonia.Data.Binding("UserName");

        // Search combos
        var anyMfr = new List<Manufacturer> { new Manufacturer { Id = 0, ManufacturerName = "(Any)" } };
        anyMfr.AddRange(_manufacturers);
        SearchMfrCombo.ItemsSource = anyMfr;
        SearchMfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        SearchMfrCombo.SelectedIndex = 0;

        var anyDesc = new List<Description> { new Description { Id = 0, DescriptionText = "(Any)" } };
        anyDesc.AddRange(_descriptions);
        SearchDescCombo.ItemsSource = anyDesc;
        SearchDescCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DescriptionText");
        SearchDescCombo.SelectedIndex = 0;

        LinkedHardwareList.ItemsSource = _linkedHardware;
        LinkedHardwareList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");

        LoadJobList();

        FilterBox.TextChanged += (_, _) => LoadJobList();
        JobList.SelectionChanged += (_, _) => OnJobSelected();
        SaveButton.Click += (_, _) => Save();
        NewButton.Click += (_, _) => ClearForm();
        DeleteButton.Click += async (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null && await DialogHelper.ConfirmAsync(window, "Delete this job?"))
                DeleteSelected();
        };

        SearchMfrCombo.SelectionChanged += (_, _) => SearchHardware();
        SearchDescCombo.SelectionChanged += (_, _) => SearchHardware();
        SearchModelBox.TextChanged += (_, _) => SearchHardware();
        AddHardwareButton.Click += (_, _) => AddHardwareToJob();
        RemoveHardwareButton.Click += (_, _) => RemoveHardwareFromJob();

        // Drag-and-drop reorder on linked hardware list
        LinkedHardwareList.AddHandler(PointerPressedEvent, OnLinkedListPointerPressed, RoutingStrategies.Tunnel);
        LinkedHardwareList.AddHandler(PointerReleasedEvent, OnLinkedListPointerReleased, RoutingStrategies.Tunnel);
        LinkedHardwareList.AddHandler(PointerMovedEvent, OnLinkedListPointerMoved, RoutingStrategies.Tunnel);
    }

    // --- Drag-and-drop reorder ---

    private void OnLinkedListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(LinkedHardwareList);
        _draggedItem = LinkedHardwareList.SelectedItem as HardwareItem;
        _isDragging = false;
    }

    private void OnLinkedListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedItem == null) return;
        var pos = e.GetPosition(LinkedHardwareList);
        var delta = pos - _dragStartPoint;
        if (!_isDragging && (System.Math.Abs(delta.Y) > 8))
            _isDragging = true;
    }

    private void OnLinkedListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging || _draggedItem == null) { _isDragging = false; _draggedItem = null; return; }

        // Determine drop target by hit-testing the release position
        var releasePos = e.GetPosition(LinkedHardwareList);
        var targetItem = GetItemAtPosition(releasePos);

        if (targetItem != null && !ReferenceEquals(targetItem, _draggedItem))
        {
            var fromIndex = _linkedHardware.IndexOf(_draggedItem);
            var toIndex = _linkedHardware.IndexOf(targetItem);
            if (fromIndex >= 0 && toIndex >= 0)
            {
                _linkedHardware.Move(fromIndex, toIndex);
            }
        }

        _isDragging = false;
        _draggedItem = null;
    }

    private HardwareItem? GetItemAtPosition(global::Avalonia.Point position)
    {
        // Walk the items and estimate position based on item height
        if (_linkedHardware.Count == 0) return null;
        var itemHeight = LinkedHardwareList.Bounds.Height / _linkedHardware.Count;
        if (itemHeight <= 0) return null;
        var index = (int)(position.Y / itemHeight);
        index = System.Math.Clamp(index, 0, _linkedHardware.Count - 1);
        return _linkedHardware[index];
    }

    // --- Job list ---

    private void LoadJobList()
    {
        var filter = FilterBox.Text?.ToLower() ?? "";
        var items = _repo!.GetAll()
            .Where(j => string.IsNullOrEmpty(filter) ||
                        j.JobNumber.ToLower().Contains(filter) ||
                        j.JobName.ToLower().Contains(filter))
            .OrderBy(j => j.JobNumber)
            .ToList();
        JobList.ItemsSource = items;
        JobList.DisplayMemberBinding = new Avalonia.Data.Binding("JobNumber");
    }

    private void OnJobSelected()
    {
        if (JobList.SelectedItem is Job j)
        {
            _selectedJobId = j.Id;
            JobNumberBox.Text = j.JobNumber;
            JobNameBox.Text = j.JobName;
            CustomerCombo.SelectedItem = _customers.FirstOrDefault(c => c.Id == j.CustomerId);
            ProjectManagerCombo.SelectedItem = _projectManagers.FirstOrDefault(pm => pm.Id == j.ProjectManagerId);
            UserProfileCombo.SelectedItem = _userProfiles.FirstOrDefault(u => u.Id == j.UserProfileId);
            StatusLabel.Text = "";
            LoadLinkedHardware();
        }
    }

    private void LoadLinkedHardware()
    {
        _linkedHardware.Clear();
        if (_selectedJobId == 0) return;

        using var context = DatabaseInitializer.CreateContext();
        var items = context.JobHardware
            .Include(jh => jh.HardwareItem)
            .ThenInclude(h => h.Manufacturer)
            .Where(jh => jh.JobId == _selectedJobId)
            .Select(jh => jh.HardwareItem)
            .ToList();

        foreach (var item in items)
            _linkedHardware.Add(item);
    }

    private void SearchHardware()
    {
        var mfr = SearchMfrCombo.SelectedItem as Manufacturer;
        var desc = SearchDescCombo.SelectedItem as Description;
        var model = SearchModelBox.Text?.Trim();

        var mfrName = (mfr == null || mfr.Id == 0) ? null : mfr.ManufacturerName;
        var descText = (desc == null || desc.Id == 0) ? null : desc.DescriptionText;

        using var context = DatabaseInitializer.CreateContext();
        var repo = new HardwareItemRepository(context);
        var results = repo.Search(mfrName, descText, model).ToList();

        HardwareSearchList.ItemsSource = results;
        HardwareSearchList.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
    }

    private void AddHardwareToJob()
    {
        if (_selectedJobId == 0) { LinkStatusLabel.Text = "Select a job first."; return; }
        if (HardwareSearchList.SelectedItem is not HardwareItem h) { LinkStatusLabel.Text = "Select a hardware item to add."; return; }

        // Increment frequency on add
        using var context = DatabaseInitializer.CreateContext();
        var freqService = new FrequencyService(context);
        var hardwareItem = context.HardwareItems.Find(h.Id);
        if (hardwareItem != null) freqService.IncrementFrequency(hardwareItem);

        _jobHardwareRepo!.Add(new JobHardware { JobId = _selectedJobId, HardwareItemId = h.Id });
        LinkStatusLabel.Text = $"Added: {h.ModelNumber}";
        LoadLinkedHardware();
    }

    private void RemoveHardwareFromJob()
    {
        if (_selectedJobId == 0) { LinkStatusLabel.Text = "Select a job first."; return; }
        if (LinkedHardwareList.SelectedItem is not HardwareItem h) { LinkStatusLabel.Text = "Select a linked item to remove."; return; }

        using var context = DatabaseInitializer.CreateContext();
        var link = context.JobHardware.FirstOrDefault(jh => jh.JobId == _selectedJobId && jh.HardwareItemId == h.Id);
        if (link != null)
        {
            context.JobHardware.Remove(link);
            context.SaveChanges();
            LinkStatusLabel.Text = $"Removed: {h.ModelNumber}";
            LoadLinkedHardware();
        }
    }

    // --- Save / Delete / Clear ---

    private void Save()
    {
        var jobNumber = JobNumberBox.Text?.Trim();
        var jobName = JobNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(jobNumber)) { StatusLabel.Text = "Job Number is required."; return; }
        if (string.IsNullOrEmpty(jobName)) { StatusLabel.Text = "Job Name is required."; return; }
        if (CustomerCombo.SelectedItem is not Customer customer) { StatusLabel.Text = "Customer is required."; return; }
        if (ProjectManagerCombo.SelectedItem is not ProjectManager pm) { StatusLabel.Text = "Project Manager is required."; return; }
        if (UserProfileCombo.SelectedItem is not UserProfile user) { StatusLabel.Text = "User Profile is required."; return; }

        if (_selectedJobId == 0)
        {
            _repo!.Add(new Job
            {
                JobNumber = jobNumber,
                JobName = jobName,
                CustomerId = customer.Id,
                ProjectManagerId = pm.Id,
                UserProfileId = user.Id
            });
        }
        else
        {
            var existing = _repo!.GetById(_selectedJobId);
            if (existing != null)
            {
                existing.JobNumber = jobNumber;
                existing.JobName = jobName;
                existing.CustomerId = customer.Id;
                existing.ProjectManagerId = pm.Id;
                existing.UserProfileId = user.Id;
                _repo.Update(existing);
            }
        }
        StatusLabel.Text = "Saved.";
        LoadJobList();
    }

    private void DeleteSelected()
    {
        if (_selectedJobId == 0) return;
        _repo!.Delete(_selectedJobId);
        ClearForm();
        LoadJobList();
    }

    private void ClearForm()
    {
        _selectedJobId = 0;
        JobNumberBox.Text = "";
        JobNameBox.Text = "";
        CustomerCombo.SelectedItem = null;
        ProjectManagerCombo.SelectedItem = null;
        UserProfileCombo.SelectedItem = null;
        StatusLabel.Text = "";
        _linkedHardware.Clear();
        JobList.SelectedItem = null;
    }
}
