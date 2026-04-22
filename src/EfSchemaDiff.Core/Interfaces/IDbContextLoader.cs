using Microsoft.EntityFrameworkCore;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public interface IDbContextLoader
{
    /// <summary>
    /// Loads a DbContext from the given assembly, extracts its EF Core schema,
    /// and returns the neutral DatabaseSchema DTO. The DbContext and its assembly
    /// load context are disposed before returning.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly containing the DbContext.</param>
    /// <param name="startupAssemblyPath">Optional path to the startup assembly for DI/config.</param>
    /// <param name="dbContextTypeName">Simple or full type name of the DbContext.</param>
    /// <param name="connectionString">Connection string injected into the DbContext options.</param>
    Task<DatabaseSchema> LoadAndExtractAsync(
        string assemblyPath,
        string? startupAssemblyPath,
        string dbContextTypeName,
        string connectionString,
        CancellationToken cancellationToken = default);
}
