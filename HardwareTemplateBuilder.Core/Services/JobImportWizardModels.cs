using System.Collections.Generic;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Describes one hardware line (a legacy <c>JobHardware</c> row) attached to a
/// <see cref="JobImportCandidate"/>, and whether the Manufacturer/Description/HardwareItem
/// it refers to already exist in the master database or will need to be created.
/// </summary>
public sealed class HardwareLineCandidate
{
    /// <summary>Gets the manufacturer name.</summary>
    public string ManufacturerName { get; init; } = string.Empty;

    /// <summary>Gets the full description hierarchy path (e.g. "Hinges\Butt Hinges").</summary>
    public string DescriptionPath { get; init; } = string.Empty;

    /// <summary>Gets the hardware item's model number.</summary>
    public string ModelNumber { get; init; } = string.Empty;

    /// <summary>Gets the optional custom label override for this line on the job.</summary>
    public string? CustomDescription { get; init; }

    /// <summary>Gets the optional remarks for this line on the job.</summary>
    public string? Remarks { get; init; }

    /// <summary>Gets the optional per-callout remarks for this line on the job.</summary>
    public string? CalloutRemarks { get; init; }

    /// <summary>Gets whether the manufacturer does not yet exist in the master database.</summary>
    public bool ManufacturerIsNew { get; init; }

    /// <summary>Gets whether the description node does not yet exist in the master database.</summary>
    public bool DescriptionIsNew { get; init; }

    /// <summary>Gets whether the hardware item itself does not yet exist in the master database.</summary>
    public bool HardwareItemIsNew { get; init; }

    /// <summary>Gets a short human-readable label for display in the wizard.</summary>
    public string DisplayLabel =>
        $"{ManufacturerName} — {ModelNumber}" + (string.IsNullOrWhiteSpace(DescriptionPath) ? string.Empty : $" ({DescriptionPath})");
}

/// <summary>Describes why a legacy job is shown (or not) in the wizard.</summary>
public enum JobCandidateStatus
{
    /// <summary>The job already exists in the master database with matching identity fields and matching hardware — nothing to do.</summary>
    AlreadyUpToDate,

    /// <summary>The job does not exist yet in the master database and will be inserted along with its hardware.</summary>
    Missing,

    /// <summary>The job exists in the master database (matching identity fields) but is missing one or more hardware links that the legacy job has.</summary>
    MissingHardwareOnly
}

/// <summary>
/// One legacy job considered for import, with its resolved Customer/ProjectManager and
/// hardware lines, annotated with what (if anything) is new relative to the master database.
/// </summary>
public sealed class JobImportCandidate
{
    /// <summary>Gets the job's Id in the legacy source database.</summary>
    public int SourceJobId { get; init; }

    /// <summary>Gets the job number.</summary>
    public string JobNumber { get; init; } = string.Empty;

    /// <summary>Gets the job name.</summary>
    public string JobName { get; init; } = string.Empty;

    /// <summary>Gets the customer name.</summary>
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>Gets whether the customer does not yet exist in the master database.</summary>
    public bool CustomerIsNew { get; init; }

    /// <summary>Gets the project manager name.</summary>
    public string ProjectManagerName { get; init; } = string.Empty;

    /// <summary>Gets whether the project manager does not yet exist in the master database.</summary>
    public bool ProjectManagerIsNew { get; init; }

    /// <summary>Gets the username of the legacy user profile that created this job (for display when filtering by creator).</summary>
    public string CreatedByUserName { get; init; } = string.Empty;

    /// <summary>Gets whether the job is complete.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Gets the job's notes, if any.</summary>
    public string? Notes { get; init; }

    /// <summary>Gets why this job is being shown to the user.</summary>
    public JobCandidateStatus Status { get; init; }

    /// <summary>
    /// Gets the matched master-database Job Id when <see cref="Status"/> is
    /// <see cref="JobCandidateStatus.MissingHardwareOnly"/>; null when the job itself is missing.
    /// </summary>
    public int? ExistingJobId { get; init; }

    /// <summary>
    /// Gets all of the legacy job's hardware lines when <see cref="Status"/> is
    /// <see cref="JobCandidateStatus.Missing"/>, or only the hardware lines missing from the
    /// existing job when <see cref="Status"/> is <see cref="JobCandidateStatus.MissingHardwareOnly"/>.
    /// </summary>
    public List<HardwareLineCandidate> HardwareLines { get; init; } = new();

    /// <summary>Gets a short one-line summary for display in a list.</summary>
    public string Summary => Status switch
    {
        JobCandidateStatus.Missing => $"{JobNumber} — {JobName}: new job, {HardwareLines.Count} hardware line(s)",
        JobCandidateStatus.MissingHardwareOnly => $"{JobNumber} — {JobName}: {HardwareLines.Count} missing hardware line(s)",
        _ => $"{JobNumber} — {JobName}: already up to date"
    };
}

/// <summary>The full result of <see cref="JobImportWizardService.PreviewAsync"/>.</summary>
public sealed class JobImportWizardPlan
{
    /// <summary>Gets the path to the legacy SQLite database file this plan was computed from.</summary>
    public string SourceFilePath { get; init; } = string.Empty;

    /// <summary>Gets the jobs that need review (missing entirely, or missing some hardware lines).</summary>
    public List<JobImportCandidate> Candidates { get; init; } = new();

    /// <summary>Gets the number of legacy jobs that were already fully up to date and skipped automatically.</summary>
    public int AlreadyUpToDateCount { get; init; }
}

/// <summary>Result of applying a single <see cref="JobImportCandidate"/>.</summary>
public sealed class JobImportApplyResult
{
    /// <summary>Gets or sets the Id the job has in the master database after the apply (new or existing).</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets how many hardware links were inserted.</summary>
    public int HardwareLinksAdded { get; set; }

    /// <summary>Gets or sets how many new HardwareItem records were created.</summary>
    public int HardwareItemsCreated { get; set; }

    /// <summary>Gets or sets how many new Manufacturer records were created.</summary>
    public int ManufacturersCreated { get; set; }

    /// <summary>Gets or sets how many new Description records were created.</summary>
    public int DescriptionsCreated { get; set; }
}
