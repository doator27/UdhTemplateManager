using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="Customer"/> entities.</summary>
public class CustomerRepository : RepositoryBase<Customer>
{
    /// <inheritdoc/>
    public CustomerRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override Customer? FindDuplicate(Customer entity) =>
        _context.Customers.FirstOrDefault(c => c.CustomerName == entity.CustomerName);
}
