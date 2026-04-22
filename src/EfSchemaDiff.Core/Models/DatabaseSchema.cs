namespace EfSchemaDiff.Core.Models;

public sealed class DatabaseSchema
{
    public IReadOnlyList<TableDefinition> Tables { get; init; } = [];
}
