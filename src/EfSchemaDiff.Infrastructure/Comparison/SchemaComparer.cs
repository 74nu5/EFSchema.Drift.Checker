using System.Text.RegularExpressions;
using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Infrastructure.Comparison;

public sealed class SchemaComparer(IStoreTypeNormalizer storeTypeNormalizer) : ISchemaComparer
{
    public SchemaDiffResult Compare(DatabaseSchema efSchema, DatabaseSchema databaseSchema, SchemaCompareOptions options)
    {
        var differences = new List<SchemaDifference>();

        var efTables = ApplyFilters(efSchema.Tables, options);
        var dbTables = ApplyFilters(databaseSchema.Tables, options);

        var dbTableLookup = dbTables
            .ToDictionary(t => TableKey(t), StringComparer.OrdinalIgnoreCase);

        foreach (var efTable in efTables)
        {
            var key = TableKey(efTable);
            if (!dbTableLookup.TryGetValue(key, out var dbTable))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.TableMissingInDatabase,
                    Severity = DiffSeverity.Error,
                    ObjectName = efTable.FullName,
                    Details = $"Table '{efTable.FullName}' exists in the EF model but not in the database.",
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name
                });
                continue;
            }

            CompareTable(efTable, dbTable, options, differences);
        }

        var efTableLookup = efTables
            .ToDictionary(t => TableKey(t), StringComparer.OrdinalIgnoreCase);

        foreach (var dbTable in dbTables)
        {
            if (!efTableLookup.ContainsKey(TableKey(dbTable)))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.TableMissingInModel,
                    Severity = DiffSeverity.Warning,
                    ObjectName = dbTable.FullName,
                    Details = $"Table '{dbTable.FullName}' exists in the database but not in the EF model.",
                    SchemaName = dbTable.Schema,
                    TableName = dbTable.Name
                });
            }
        }

        return new SchemaDiffResult { Differences = differences };
    }

    private void CompareTable(
        TableDefinition efTable,
        TableDefinition dbTable,
        SchemaCompareOptions options,
        List<SchemaDifference> differences)
    {
        CompareColumns(efTable, dbTable, options, differences);

        if (!efTable.IsKeyless)
        {
            ComparePrimaryKeys(efTable, dbTable, differences);
            CompareForeignKeys(efTable, dbTable, differences);
            CompareIndexes(efTable, dbTable, differences);
            CompareUniqueConstraints(efTable, dbTable, differences);
        }
    }

    private void CompareColumns(
        TableDefinition efTable,
        TableDefinition dbTable,
        SchemaCompareOptions options,
        List<SchemaDifference> differences)
    {
        var efColumns = efTable.Columns
            .Where(c => !ShouldIgnoreColumn(efTable, c, options))
            .Where(c => !c.IsPeriodStart && !c.IsPeriodEnd)
            .ToList();

        var dbColumns = dbTable.Columns
            .Where(c => !ShouldIgnoreColumn(dbTable, c, options))
            .Where(c => !c.IsPeriodStart && !c.IsPeriodEnd)
            .ToList();

        var dbColumnLookup = dbColumns
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var efCol in efColumns)
        {
            if (!dbColumnLookup.TryGetValue(efCol.Name, out var dbCol))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.ColumnMissingInDatabase,
                    Severity = DiffSeverity.Error,
                    ObjectName = ColumnObjectName(efTable, efCol),
                    Details = $"Column '{efCol.Name}' in table '{efTable.FullName}' exists in the EF model but not in the database.",
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ColumnName = efCol.Name
                });
                continue;
            }

            if (!efCol.IsComputed)
            {
                var efType = storeTypeNormalizer.Normalize(efCol.StoreType);
                var dbType = storeTypeNormalizer.Normalize(dbCol.StoreType);
                if (!string.Equals(efType, dbType, StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add(new SchemaDifference
                    {
                        Type = DiffType.ColumnTypeMismatch,
                        Severity = DiffSeverity.Error,
                        ObjectName = ColumnObjectName(efTable, efCol),
                        Details = $"Column '{efCol.Name}' in '{efTable.FullName}' has type mismatch.",
                        ExpectedValue = efType,
                        ActualValue = dbType,
                        SchemaName = efTable.Schema,
                        TableName = efTable.Name,
                        ColumnName = efCol.Name
                    });
                }
            }

            if (efCol.IsNullable != dbCol.IsNullable)
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.NullabilityMismatch,
                    Severity = DiffSeverity.Error,
                    ObjectName = ColumnObjectName(efTable, efCol),
                    Details = $"Column '{efCol.Name}' in '{efTable.FullName}' has nullability mismatch.",
                    ExpectedValue = efCol.IsNullable.ToString(),
                    ActualValue = dbCol.IsNullable.ToString(),
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ColumnName = efCol.Name
                });
            }

            if (efCol.MaxLength.HasValue && dbCol.MaxLength.HasValue && efCol.MaxLength != dbCol.MaxLength)
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.MaxLengthMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = ColumnObjectName(efTable, efCol),
                    Details = $"Column '{efCol.Name}' in '{efTable.FullName}' has max-length mismatch.",
                    ExpectedValue = efCol.MaxLength.ToString(),
                    ActualValue = dbCol.MaxLength.ToString(),
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ColumnName = efCol.Name
                });
            }

            if (efCol.Precision.HasValue && dbCol.Precision.HasValue && efCol.Precision != dbCol.Precision)
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.PrecisionMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = ColumnObjectName(efTable, efCol),
                    Details = $"Column '{efCol.Name}' in '{efTable.FullName}' has precision mismatch.",
                    ExpectedValue = efCol.Precision.ToString(),
                    ActualValue = dbCol.Precision.ToString(),
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ColumnName = efCol.Name
                });
            }

            if (efCol.Scale.HasValue && dbCol.Scale.HasValue && efCol.Scale != dbCol.Scale)
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.ScaleMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = ColumnObjectName(efTable, efCol),
                    Details = $"Column '{efCol.Name}' in '{efTable.FullName}' has scale mismatch.",
                    ExpectedValue = efCol.Scale.ToString(),
                    ActualValue = dbCol.Scale.ToString(),
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ColumnName = efCol.Name
                });
            }
        }

        var efColumnLookup = efColumns
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var dbCol in dbColumns)
        {
            if (!efColumnLookup.ContainsKey(dbCol.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.ColumnMissingInModel,
                    Severity = DiffSeverity.Warning,
                    ObjectName = ColumnObjectName(dbTable, dbCol),
                    Details = $"Column '{dbCol.Name}' in table '{dbTable.FullName}' exists in the database but not in the EF model.",
                    SchemaName = dbTable.Schema,
                    TableName = dbTable.Name,
                    ColumnName = dbCol.Name
                });
            }
        }
    }

    private static void ComparePrimaryKeys(
        TableDefinition efTable,
        TableDefinition dbTable,
        List<SchemaDifference> differences)
    {
        var efPk = new HashSet<string>(efTable.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var dbPk = new HashSet<string>(dbTable.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        if (!efPk.SetEquals(dbPk))
        {
            differences.Add(new SchemaDifference
            {
                Type = DiffType.PrimaryKeyMismatch,
                Severity = DiffSeverity.Error,
                ObjectName = efTable.FullName,
                Details = $"Primary key mismatch on table '{efTable.FullName}'.",
                ExpectedValue = string.Join(", ", efTable.PrimaryKeyColumns.OrderBy(c => c)),
                ActualValue = string.Join(", ", dbTable.PrimaryKeyColumns.OrderBy(c => c)),
                SchemaName = efTable.Schema,
                TableName = efTable.Name
            });
        }
    }

    private static void CompareForeignKeys(
        TableDefinition efTable,
        TableDefinition dbTable,
        List<SchemaDifference> differences)
    {
        var dbFkLookup = dbTable.ForeignKeys
            .ToDictionary(fk => fk.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var efFk in efTable.ForeignKeys)
        {
            if (!dbFkLookup.ContainsKey(efFk.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.ForeignKeyMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = efTable.FullName,
                    Details = $"Foreign key '{efFk.Name}' on table '{efTable.FullName}' exists in the EF model but not in the database.",
                    ExpectedValue = efFk.Name,
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ConstraintName = efFk.Name
                });
            }
        }

        var efFkLookup = efTable.ForeignKeys
            .ToDictionary(fk => fk.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var dbFk in dbTable.ForeignKeys)
        {
            if (!efFkLookup.ContainsKey(dbFk.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.ForeignKeyMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = dbTable.FullName,
                    Details = $"Foreign key '{dbFk.Name}' on table '{dbTable.FullName}' exists in the database but not in the EF model.",
                    ActualValue = dbFk.Name,
                    SchemaName = dbTable.Schema,
                    TableName = dbTable.Name,
                    ConstraintName = dbFk.Name
                });
            }
        }
    }

    private static void CompareIndexes(
        TableDefinition efTable,
        TableDefinition dbTable,
        List<SchemaDifference> differences)
    {
        var dbIndexLookup = dbTable.Indexes
            .ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var efIdx in efTable.Indexes)
        {
            if (!dbIndexLookup.ContainsKey(efIdx.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.IndexMissingInDatabase,
                    Severity = DiffSeverity.Warning,
                    ObjectName = efTable.FullName,
                    Details = $"Index '{efIdx.Name}' on table '{efTable.FullName}' exists in the EF model but not in the database.",
                    ExpectedValue = efIdx.Name,
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    IndexName = efIdx.Name
                });
            }
        }

        var efIndexLookup = efTable.Indexes
            .ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var dbIdx in dbTable.Indexes)
        {
            if (!efIndexLookup.ContainsKey(dbIdx.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.IndexMissingInModel,
                    Severity = DiffSeverity.Info,
                    ObjectName = dbTable.FullName,
                    Details = $"Index '{dbIdx.Name}' on table '{dbTable.FullName}' exists in the database but not in the EF model.",
                    ActualValue = dbIdx.Name,
                    SchemaName = dbTable.Schema,
                    TableName = dbTable.Name,
                    IndexName = dbIdx.Name
                });
            }
        }
    }

    private static void CompareUniqueConstraints(
        TableDefinition efTable,
        TableDefinition dbTable,
        List<SchemaDifference> differences)
    {
        var dbUcLookup = dbTable.UniqueConstraints
            .ToDictionary(uc => uc.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var efUc in efTable.UniqueConstraints)
        {
            if (!dbUcLookup.ContainsKey(efUc.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.UniqueConstraintMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = efTable.FullName,
                    Details = $"Unique constraint '{efUc.Name}' on table '{efTable.FullName}' exists in the EF model but not in the database.",
                    ExpectedValue = efUc.Name,
                    SchemaName = efTable.Schema,
                    TableName = efTable.Name,
                    ConstraintName = efUc.Name
                });
            }
        }

        var efUcLookup = efTable.UniqueConstraints
            .ToDictionary(uc => uc.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var dbUc in dbTable.UniqueConstraints)
        {
            if (!efUcLookup.ContainsKey(dbUc.Name))
            {
                differences.Add(new SchemaDifference
                {
                    Type = DiffType.UniqueConstraintMismatch,
                    Severity = DiffSeverity.Warning,
                    ObjectName = dbTable.FullName,
                    Details = $"Unique constraint '{dbUc.Name}' on table '{dbTable.FullName}' exists in the database but not in the EF model.",
                    ActualValue = dbUc.Name,
                    SchemaName = dbTable.Schema,
                    TableName = dbTable.Name,
                    ConstraintName = dbUc.Name
                });
            }
        }
    }

    private static IReadOnlyList<TableDefinition> ApplyFilters(
        IReadOnlyList<TableDefinition> tables,
        SchemaCompareOptions options)
    {
        var filtered = tables.AsEnumerable();

        if (options.Schema != null)
            filtered = filtered.Where(t => string.Equals(t.Schema, options.Schema, StringComparison.OrdinalIgnoreCase));

        if (options.ExcludeNavigationTables)
            filtered = filtered.Where(t => !t.IsImplicitJoinTable);

        if (options.IgnoreTables.Count > 0)
            filtered = filtered.Where(t => !options.IgnoreTables.Any(p => MatchesGlob(p, TableGlobKey(t))));

        return filtered.ToList();
    }

    private static bool ShouldIgnoreColumn(TableDefinition table, ColumnDefinition column, SchemaCompareOptions options)
    {
        if (options.IgnoreColumns.Count == 0)
            return false;

        var schemaTableColumn = table.Schema is null
            ? $"{table.Name}.{column.Name}"
            : $"{table.Schema}.{table.Name}.{column.Name}";

        return options.IgnoreColumns.Any(p => MatchesGlob(p, schemaTableColumn));
    }

    private static string TableKey(TableDefinition table) =>
        table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}";

    private static string TableGlobKey(TableDefinition table) =>
        table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}";

    private static string ColumnObjectName(TableDefinition table, ColumnDefinition column) =>
        table.Schema is null
            ? $"{table.Name}.{column.Name}"
            : $"{table.Schema}.{table.Name}.{column.Name}";

    private static bool MatchesGlob(string pattern, string value)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
    }
}
