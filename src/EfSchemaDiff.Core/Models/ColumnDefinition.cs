namespace EfSchemaDiff.Core.Models;

public sealed class ColumnDefinition
{
    public required string Name { get; init; }

    /// <summary>Normalized store type (e.g. "nvarchar(200)", "int", "decimal(18,2)").</summary>
    public string? StoreType { get; init; }

    public bool IsNullable { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public bool IsComputed { get; init; }
    public string? ComputedColumnSql { get; init; }
    public bool IsRowVersion { get; init; }

    /// <summary>True for EF shadow properties (not declared in the entity class).</summary>
    public bool IsShadow { get; init; }

    public string? DefaultValueSql { get; init; }

    /// <summary>True if the column is a JSON column.</summary>
    public bool IsJson { get; init; }

    /// <summary>Temporal table period column (Start or End).</summary>
    public bool IsPeriodStart { get; init; }
    public bool IsPeriodEnd { get; init; }
}
