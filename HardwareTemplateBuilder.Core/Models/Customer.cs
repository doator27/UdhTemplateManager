using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a customer associated with one or more jobs.
/// </summary>
public class Customer
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the customer's name.</summary>
    [Required]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the jobs associated with this customer.</summary>
    public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
