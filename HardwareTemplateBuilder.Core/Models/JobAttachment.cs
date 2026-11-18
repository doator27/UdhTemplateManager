using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a file (email export or PDF) attached to a <see cref="Job"/>.
/// The file is copied into the job's <c>Attachments/</c> subfolder on disk so the
/// job folder stays self-contained.
/// </summary>
public class JobAttachment
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the parent job.</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets the parent job.</summary>
    public Job Job { get; set; } = null!;

    /// <summary>
    /// Gets or sets the job release (addendum) the file belongs to: a plain id, null = the base list.
    /// The job page shows the active release's attachments; release files are stored under
    /// <c>Attachments/{ReleaseLabel}/</c>.
    /// </summary>
    public int? ReleaseId { get; set; }

    /// <summary>Gets or sets the original file name (used as the display label).</summary>
    [Required]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path to the copied file inside the job's
    /// <c>Attachments/</c> subfolder.
    /// </summary>
    [Required]
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the category — <c>"Email"</c> or <c>"PDF"</c>.</summary>
    [Required]
    public string FileType { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description of the attachment's contents.</summary>
    public string? Notes { get; set; }

    /// <summary>Gets or sets the UTC date and time the attachment was added.</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the release name shown in front of the label when the list shows every
    /// release's attachments (not stored).
    /// </summary>
    [NotMapped]
    public string? ReleaseText { get; set; }

    /// <summary>
    /// Returns a single-line display string combining the type tag, file name,
    /// and note (if any).
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var base_ = $"[{FileType}]  {FileName}";
            if (!string.IsNullOrEmpty(ReleaseText)) base_ = $"{ReleaseText}:  {base_}";
            return string.IsNullOrWhiteSpace(Notes) ? base_ : $"{base_}  —  {Notes}";
        }
    }
}
