namespace EfSchemaDiff.Core.Models;

public sealed class ForeignKeyDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required string PrincipalTable { get; init; }
    public string? PrincipalSchema { get; init; }
    public required IReadOnlyList<string> PrincipalColumns { get; init; }
    public string DeleteBehavior { get; init; } = "NoAction";
}
