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

        LoadCustomers();
        LoadProjectManagers();
        LoadJobList();

        MainMenuButton.Click += (_, _) => NavigationRequested?.Invoke("Dashboard");
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

        AddCustomerButton.Click        += (_, _) => TogglePanel(NewCustomerPanel, NewCustomerBox);
        CancelCustomerButton.Click     += (_, _) => HidePanel(NewCustomerPanel, NewCustomerBox);
        SaveCustomerButton.Click       += (_, _) => AddCustomer();

        AddProjectManagerButton.Click      += (_, _) => TogglePanel(NewProjectManagerPanel, NewProjectManagerBox);
        CancelProjectManagerButton.Click   += (_, _) => HidePanel(NewProjectManagerPanel, NewProjectManagerBox);
        SaveProjectManagerButton.Click     += (_, _) => AddProjectManager();
    }

    private void LoadCustomers(Customer? selectAfter = null)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _customers = new CustomerRepository(ctx).GetAll().OrderBy(c => c.CustomerName).ToList();
        CustomerCombo.ItemsSource = _customers;
        CustomerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("CustomerName");
        if (selectAfter != null)
            CustomerCombo.SelectedItem = _customers.FirstOrDefault(c => c.Id == selectAfter.Id);
    }

    private void LoadProjectManagers(ProjectManager? selectAfter = null)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _projectManagers = new ProjectManagerRepository(ctx).GetAll().OrderBy(pm => pm.ProjectManagerName).ToList();
        ProjectManagerCombo.ItemsSource = _projectManagers;
        ProjectManagerCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ProjectManagerName");
        if (selectAfter != null)
            ProjectManagerCombo.SelectedItem = _projectManagers.FirstOrDefault(pm => pm.Id == selectAfter.Id);
    }

    private void AddCustomer()
    {
        var name = NewCustomerBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter a customer name."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var saved = new CustomerRepository(ctx).Add(new Customer { CustomerName = name });
        HidePanel(NewCustomerPanel, NewCustomerBox);
        LoadCustomers(saved);
        StatusLabel.Text = $"Customer \"{saved.CustomerName}\" added.";
    }

    private void AddProjectManager()
    {
        var name = NewProjectManagerBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter a project manager name."; return; }
        using var ctx = DatabaseInitializer.CreateContext();
        var saved = new ProjectManagerRepository(ctx).Add(new ProjectManager { ProjectManagerName = name });
        HidePanel(NewProjectManagerPanel, NewProjectManagerBox);
        LoadProjectManagers(saved);
        StatusLabel.Text = $"Project manager \"{saved.ProjectManagerName}\" added.";
    }

    private static void TogglePanel(Avalonia.Controls.Control panel, TextBox box)
    {
        panel.IsVisible = !panel.IsVisible;
        if (panel.IsVisible) box.Focus();
    }

    private static void HidePanel(Avalonia.Controls.Control panel, TextBox box)
    {
        panel.IsVisible = false;
        box.Text = "";
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
