using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Service that increments the usage frequency counter on <see cref="HardwareItem"/> records.
/// Frequency is used to sort search results so the most-used items appear first.
/// </summary>
public class FrequencyService
{
    private readonly AppDbContext _context;

    /// <summary>Initializes a new instance of <see cref="FrequencyService"/>.</summary>
    /// <param name="context">The database context.</param>
    public FrequencyService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Increments the <see cref="HardwareItem.Frequency"/> counter for the given hardware item
    /// and persists the change to the database.
    /// </summary>
    /// <param name="hardwareItem">The hardware item to increment.</param>
    public void IncrementFrequency(HardwareItem hardwareItem)
    {
        hardwareItem.Frequency++;
        _context.HardwareItems.Update(hardwareItem);
        _context.SaveChanges();
    }
}
