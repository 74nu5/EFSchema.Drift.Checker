namespace EfSchemaDiff.Core.Models;

/// <summary>Represents a unique constraint (not a unique index) on a table.</summary>
public sealed class UniqueConstraintDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
}
