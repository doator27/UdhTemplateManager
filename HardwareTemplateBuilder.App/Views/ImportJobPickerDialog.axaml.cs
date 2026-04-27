using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Modal dialog that lets the user pick a source job to import hardware from.
/// Returns the selected <see cref="Job"/>, or <c>null</c> if cancelled.
/// </summary>
public partial class ImportJobPickerDialog : Window
{
    private readonly int _currentJobId;
    private List<Job> _allJobs = new();

    /// <summary>Required by the Avalonia XAML compiler.</summary>
    public ImportJobPickerDialog() : this(0) { }

    /// <summary>Initializes the dialog, excluding the current job from the list.</summary>
    /// <param name="currentJobId">The ID of the job being imported into (excluded from the picker).</param>
    public ImportJobPickerDialog(int currentJobId)
    {
        _currentJobId = currentJobId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _allJobs = new JobRepository(ctx).GetAll()
            .Where(j => j.Id != _currentJobId)
            .OrderBy(j => j.JobNumber)
            .ToList();

        RefreshList(_allJobs);

        FilterBox.TextChanged += (_, _) => ApplyFilter();
        ImportButton.Click    += (_, _) => Confirm();
        CancelButton.Click    += (_, _) => Close(null);

        if (JobList.ItemCount > 0)
            JobList.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        var term = FilterBox.Text?.Trim().ToLowerInvariant() ?? "";
        var filtered = string.IsNullOrEmpty(term)
            ? _allJobs
            : _allJobs.Where(j =>
                j.JobNumber.ToLowerInvariant().Contains(term) ||
                j.JobName.ToLowerInvariant().Contains(term)).ToList();
        RefreshList(filtered);
    }

    private void RefreshList(List<Job> jobs)
    {
        JobList.ItemsSource = null;
        JobList.ItemsSource = jobs;
        JobList.DisplayMemberBinding = new Avalonia.Data.Binding("JobNumber");

        if (JobList.ItemCount > 0)
            JobList.SelectedIndex = 0;
    }

    private void Confirm()
    {
        if (JobList.SelectedItem is not Job selected)
        {
            StatusLabel.Text = "Select a job first.";
            return;
        }
        Close(selected);
    }
}
