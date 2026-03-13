using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HardwareTemplateBuilder.App;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// List and metadata-editor view for <see cref="Job"/> records.
/// Select a job and click "Open Job" to navigate to <see cref="JobDetailView"/>.
/// </summary>
public partial class JobsView : UserControl
{
    private JobRepository? _repo;
    private List<Customer> _customers = new();
    private List<ProjectManager> _projectManagers = new();
    private int _selectedJobId;

    /// <summary>Raised when the user requests navigation to a named view (e.g. "JobDetail:42").</summary>
    public event System.Action<string>? NavigationRequested;

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
        _customers = new CustomerRepository(context).GetAll().OrderBy(c => c.CustomerName).ToList();
        _projectManagers = new ProjectManagerRepository(context).GetAll().OrderBy(pm => pm.ProjectManagerName).ToList();

        CustomerCombo.ItemsSource = _customers;
        CustomerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("CustomerName");
        ProjectManagerCombo.ItemsSource = _projectManagers;
        ProjectManagerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ProjectManagerName");

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
        OpenJobButton.Click += (_, _) =>
        {
            if (_selectedJobId == 0) { StatusLabel.Text = "Select a job to open."; return; }
            NavigationRequested?.Invoke($"JobDetail:{_selectedJobId}");
        };
    }

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
            StatusLabel.Text = "";
        }
    }

    private void Save()
    {
        var jobNumber = JobNumberBox.Text?.Trim();
        var jobName = JobNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(jobNumber)) { StatusLabel.Text = "Job Number is required."; return; }
        if (string.IsNullOrEmpty(jobName)) { StatusLabel.Text = "Job Name is required."; return; }
        if (CustomerCombo.SelectedItem is not Customer customer) { StatusLabel.Text = "Customer is required."; return; }
        if (ProjectManagerCombo.SelectedItem is not ProjectManager pm) { StatusLabel.Text = "Project Manager is required."; return; }

        var userProfileId = SessionService.ActiveUserProfile?.Id ?? 0;

        if (_selectedJobId == 0)
        {
            _repo!.Add(new Job
            {
                JobNumber        = jobNumber,
                JobName          = jobName,
                CustomerId       = customer.Id,
                ProjectManagerId = pm.Id,
                UserProfileId    = userProfileId
            });
        }
        else
        {
            var existing = _repo!.GetById(_selectedJobId);
            if (existing != null)
            {
                existing.JobNumber        = jobNumber;
                existing.JobName          = jobName;
                existing.CustomerId       = customer.Id;
                existing.ProjectManagerId = pm.Id;
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
        StatusLabel.Text = "";
        JobList.SelectedItem = null;
    }
}
