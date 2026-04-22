using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public interface IDatabaseSchemaReader
{
    Task<DatabaseSchema> ReadAsync(string connectionString, CancellationToken cancellationToken = default);
}
