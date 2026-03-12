using System.Collections.Generic;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Generic repository interface providing standard CRUD operations for any entity type.
/// </summary>
/// <typeparam name="T">The entity type managed by this repository.</typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>Gets an entity by its primary key.</summary>
    /// <param name="id">The primary key value.</param>
    /// <returns>The entity, or null if not found.</returns>
    T? GetById(int id);

    /// <summary>Gets all entities of this type.</summary>
    /// <returns>A list of all entities.</returns>
    IEnumerable<T> GetAll();

    /// <summary>
    /// Adds a new entity. If a duplicate is detected, returns the existing record instead.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <returns>The added or existing entity.</returns>
    T Add(T entity);

    /// <summary>Updates an existing entity.</summary>
    /// <param name="entity">The entity with updated values.</param>
    void Update(T entity);

    /// <summary>Deletes an entity by its primary key.</summary>
    /// <param name="id">The primary key of the entity to delete.</param>
    void Delete(int id);
}
