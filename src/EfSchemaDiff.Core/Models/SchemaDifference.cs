namespace EfSchemaDiff.Core.Models;

public sealed class SchemaDifference
{
    public required DiffType Type { get; init; }
    public required DiffSeverity Severity { get; init; }

    /// <summary>Human-readable object path (e.g. "dbo.Users.Email" or "dbo.Users").</summary>
    public required string ObjectName { get; init; }

    public required string Details { get; init; }

    /// <summary>Expected value from the EF model.</summary>
    public string? ExpectedValue { get; init; }

    /// <summary>Actual value found in the database.</summary>
    public string? ActualValue { get; init; }

    // Structured location fields for SARIF and machine-readable output
    public string? SchemaName { get; init; }
    public string? TableName { get; init; }
    public string? ColumnName { get; init; }
    public string? ConstraintName { get; init; }
    public string? IndexName { get; init; }
}
