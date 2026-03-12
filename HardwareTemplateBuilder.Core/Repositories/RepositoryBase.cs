using HardwareTemplateBuilder.Core.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Abstract base implementation of <see cref="IRepository{T}"/> using Entity Framework Core.
/// Concrete repositories override <see cref="FindDuplicate"/> to enforce redundancy checks.
/// </summary>
/// <typeparam name="T">The entity type managed by this repository.</typeparam>
public abstract class RepositoryBase<T> : IRepository<T> where T : class
{
    /// <summary>The shared database context.</summary>
    protected readonly AppDbContext _context;

    /// <summary>Initializes a new instance of <see cref="RepositoryBase{T}"/>.</summary>
    /// <param name="context">The EF Core database context.</param>
    protected RepositoryBase(AppDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc/>
    public T? GetById(int id) => _context.Set<T>().Find(id);

    /// <inheritdoc/>
    public IEnumerable<T> GetAll() => _context.Set<T>().ToList();

    /// <inheritdoc/>
    public T Add(T entity)
    {
        var existing = FindDuplicate(entity);
        if (existing != null)
            return existing;

        _context.Set<T>().Add(entity);
        _context.SaveChanges();
        return entity;
    }

    /// <inheritdoc/>
    public void Update(T entity)
    {
        _context.Set<T>().Update(entity);
        _context.SaveChanges();
    }

    /// <inheritdoc/>
    public void Delete(int id)
    {
        var entity = GetById(id);
        if (entity != null)
        {
            _context.Set<T>().Remove(entity);
            _context.SaveChanges();
        }
    }

    /// <summary>
    /// Searches for a duplicate of the given entity in the database.
    /// Returns the existing entity if found, or null if no duplicate exists.
    /// Override in concrete repositories to define what constitutes a duplicate.
    /// </summary>
    /// <param name="entity">The entity to check for duplicates.</param>
    /// <returns>The existing duplicate entity, or null.</returns>
    protected virtual T? FindDuplicate(T entity) => null;
}
