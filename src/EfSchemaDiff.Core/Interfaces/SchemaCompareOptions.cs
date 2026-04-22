using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public sealed class SchemaCompareOptions
{
    /// <summary>Only compare tables in this schema. Null means all schemas.</summary>
    public string? Schema { get; init; }

    /// <summary>Glob patterns for tables to ignore (e.g. "__EFMigrationsHistory", "Hangfire.*").</summary>
    public IReadOnlyList<string> IgnoreTables { get; init; } = [];

    /// <summary>Glob patterns for columns to ignore (e.g. "*.CreatedAt", "Audit_*").</summary>
    public IReadOnlyList<string> IgnoreColumns { get; init; } = [];

    /// <summary>Skip auto-generated many-to-many join tables.</summary>
    public bool ExcludeNavigationTables { get; init; }
}
