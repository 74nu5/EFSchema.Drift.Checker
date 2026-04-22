namespace EfSchemaDiff.Core.Models;

public sealed class IndexDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public bool IsUnique { get; init; }
    public bool IsClustered { get; init; }

    /// <summary>SQL WHERE clause for filtered indexes.</summary>
    public string? Filter { get; init; }
}
