using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;
using Microsoft.Data.SqlClient;

namespace EfSchemaDiff.Infrastructure.Providers.SqlServer;

public sealed class SqlServerSchemaReader : IDatabaseSchemaReader
{
    private sealed record RawTable(string Schema, string Name, bool IsView);

    private sealed record RawColumn(
        string Schema,
        string Table,
        string Name,
        string DataType,
        int? CharMaxLength,
        int? NumericPrecision,
        int? NumericScale,
        int? DatetimePrecision,
        bool IsNullable,
        string? DefaultValueSql);

    public async Task<DatabaseSchema> ReadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables        = await ReadTablesAsync(connection, cancellationToken);
        var columnsByTable = await ReadColumnsAsync(connection, cancellationToken);
        var computedByTable = await ReadComputedColumnsAsync(connection, cancellationToken);
        var pkByTable     = await ReadPrimaryKeysAsync(connection, cancellationToken);
        var uqByTable     = await ReadUniqueConstraintsAsync(connection, cancellationToken);
        var fkByTable     = await ReadForeignKeysAsync(connection, cancellationToken);
        var idxByTable    = await ReadIndexesAsync(connection, cancellationToken);
        var temporalTables = await ReadTemporalTablesAsync(connection, cancellationToken);
        var periodsByTable = await ReadPeriodColumnsAsync(connection, cancellationToken);

        var tableDefs = new List<TableDefinition>(tables.Count);

        foreach (var t in tables)
        {
            var key = TableKey(t.Schema, t.Name);
            var rawCols  = columnsByTable.GetValueOrDefault(key, []);
            var computed = computedByTable.GetValueOrDefault(key, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            var (periodStart, periodEnd) = periodsByTable.GetValueOrDefault(key, (null, null));

            var columnDefs = rawCols.Select(c =>
            {
                var dataTypeLower = c.DataType.ToLowerInvariant();
                var isComputed    = computed.TryGetValue(c.Name, out var computedSql);
                var isRowVersion  = dataTypeLower is "timestamp" or "rowversion";
                var isJson        = dataTypeLower == "json";

                var (precision, scale) = dataTypeLower switch
                {
                    "decimal" or "numeric"                    => (c.NumericPrecision, c.NumericScale),
                    "datetime2" or "datetimeoffset" or "time" => (c.DatetimePrecision, (int?)null),
                    _                                         => ((int?)null, (int?)null)
                };

                var maxLength = dataTypeLower switch
                {
                    "char" or "nchar" or "varchar" or "nvarchar" or "binary" or "varbinary" =>
                        c.CharMaxLength is -1 ? null : c.CharMaxLength,
                    _ => null
                };

                return new ColumnDefinition
                {
                    Name              = c.Name,
                    StoreType         = BuildStoreType(c.DataType, c.CharMaxLength, c.NumericPrecision, c.NumericScale, c.DatetimePrecision),
                    IsNullable        = c.IsNullable,
                    MaxLength         = maxLength,
                    Precision         = precision,
                    Scale             = scale,
                    IsComputed        = isComputed,
                    ComputedColumnSql = isComputed ? computedSql : null,
                    IsRowVersion      = isRowVersion,
                    DefaultValueSql   = c.DefaultValueSql,
                    IsJson            = isJson,
                    IsPeriodStart     = string.Equals(c.Name, periodStart, StringComparison.OrdinalIgnoreCase),
                    IsPeriodEnd       = string.Equals(c.Name, periodEnd, StringComparison.OrdinalIgnoreCase),
                };
            }).ToList();

            tableDefs.Add(new TableDefinition
            {
                Name              = t.Name,
                Schema            = t.Schema,
                Columns           = columnDefs,
                PrimaryKeyColumns = pkByTable.GetValueOrDefault(key, []),
                ForeignKeys       = fkByTable.GetValueOrDefault(key, []),
                Indexes           = idxByTable.GetValueOrDefault(key, []),
                UniqueConstraints = uqByTable.GetValueOrDefault(key, []),
                IsView            = t.IsView,
                IsTemporal        = temporalTables.Contains(key),
            });
        }

        return new DatabaseSchema { Tables = tableDefs };
    }

    private static string TableKey(string schema, string name) => $"{schema}.{name}";

    private static string BuildStoreType(
        string dataType,
        int? charMaxLength,
        int? numericPrecision,
        int? numericScale,
        int? datetimePrecision)
    {
        return dataType.ToLowerInvariant() switch
        {
            "char" or "nchar" or "varchar" or "nvarchar" or "binary" or "varbinary" =>
                charMaxLength is -1 ? $"{dataType}(max)" : $"{dataType}({charMaxLength})",

            "decimal" or "numeric" =>
                numericPrecision.HasValue && numericScale.HasValue
                    ? $"{dataType}({numericPrecision},{numericScale})"
                    : dataType,

            "datetime2" or "datetimeoffset" or "time" =>
                datetimePrecision.HasValue ? $"{dataType}({datetimePrecision})" : dataType,

            _ => dataType
        };
    }

    // ── Readers ───────────────────────────────────────────────────────────────

    private static async Task<List<RawTable>> ReadTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;

        var result = new List<RawTable>();
        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            result.Add(new RawTable(reader.GetString(0), reader.GetString(1), reader.GetString(2) == "VIEW"));

        return result;
    }

    private static async Task<Dictionary<string, List<RawColumn>>> ReadColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                TABLE_SCHEMA,
                TABLE_NAME,
                COLUMN_NAME,
                DATA_TYPE,
                CHARACTER_MAXIMUM_LENGTH,
                NUMERIC_PRECISION,
                NUMERIC_SCALE,
                DATETIME_PRECISION,
                IS_NULLABLE,
                COLUMN_DEFAULT
            FROM INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
            """;

        var result = new Dictionary<string, List<RawColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var table  = reader.GetString(1);
            var key    = TableKey(schema, table);

            if (!result.TryGetValue(key, out var cols))
                result[key] = cols = [];

            cols.Add(new RawColumn(
                schema,
                table,
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),          // CHARACTER_MAXIMUM_LENGTH (int)
                reader.IsDBNull(5) ? null : (int)reader.GetByte(5),      // NUMERIC_PRECISION (tinyint → int)
                reader.IsDBNull(6) ? null : reader.GetInt32(6),          // NUMERIC_SCALE (int)
                reader.IsDBNull(7) ? null : (int)reader.GetInt16(7),     // DATETIME_PRECISION (smallint → int)
                reader.GetString(8) == "YES",
                reader.IsDBNull(9) ? null : reader.GetString(9)
            ));
        }

        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> ReadComputedColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                SCHEMA_NAME(t.schema_id) AS TableSchema,
                t.name                   AS TableName,
                c.name                   AS ColumnName,
                c.definition             AS ComputedSql
            FROM sys.computed_columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            """;

        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var key = TableKey(reader.GetString(0), reader.GetString(1));
            if (!result.TryGetValue(key, out var cols))
                result[key] = cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            cols[reader.GetString(2)] = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        }

        return result;
    }

    private static async Task<Dictionary<string, List<string>>> ReadPrimaryKeysAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                tc.TABLE_SCHEMA,
                tc.TABLE_NAME,
                kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON  tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA    = kcu.TABLE_SCHEMA
                AND tc.TABLE_NAME      = kcu.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY tc.TABLE_SCHEMA, tc.TABLE_NAME, kcu.ORDINAL_POSITION
            """;

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var key = TableKey(reader.GetString(0), reader.GetString(1));
            if (!result.TryGetValue(key, out var cols))
                result[key] = cols = [];
            cols.Add(reader.GetString(2));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<UniqueConstraintDefinition>>> ReadUniqueConstraintsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                tc.TABLE_SCHEMA,
                tc.TABLE_NAME,
                tc.CONSTRAINT_NAME,
                kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON  tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA    = kcu.TABLE_SCHEMA
                AND tc.TABLE_NAME      = kcu.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'UNIQUE'
            ORDER BY tc.TABLE_SCHEMA, tc.TABLE_NAME, tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION
            """;

        // tableKey → constraintName → columns
        var raw = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableKey       = TableKey(reader.GetString(0), reader.GetString(1));
            var constraintName = reader.GetString(2);

            if (!raw.TryGetValue(tableKey, out var constraints))
                raw[tableKey] = constraints = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (!constraints.TryGetValue(constraintName, out var cols))
                constraints[constraintName] = cols = [];

            cols.Add(reader.GetString(3));
        }

        return raw.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                       .Select(c => new UniqueConstraintDefinition { Name = c.Key, Columns = c.Value })
                       .ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, List<ForeignKeyDefinition>>> ReadForeignKeysAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                fk.name                               AS FKName,
                SCHEMA_NAME(t.schema_id)              AS TableSchema,
                t.name                                AS TableName,
                c.name                                AS ColumnName,
                fkc.constraint_column_id              AS OrdinalPosition,
                SCHEMA_NAME(rt.schema_id)             AS RefSchema,
                rt.name                               AS RefTable,
                rc.name                               AS RefColumn,
                fk.delete_referential_action_desc     AS DeleteAction
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fk.object_id             = fkc.constraint_object_id
            INNER JOIN sys.tables t                ON fk.parent_object_id      = t.object_id
            INNER JOIN sys.columns c               ON fkc.parent_object_id     = c.object_id AND fkc.parent_column_id     = c.column_id
            INNER JOIN sys.tables rt               ON fk.referenced_object_id  = rt.object_id
            INNER JOIN sys.columns rc              ON fkc.referenced_object_id = rc.object_id AND fkc.referenced_column_id = rc.column_id
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        // tableKey → fkName → (principalSchema, principalTable, deleteAction, fkCols, principalCols)
        var raw = new Dictionary<string, Dictionary<string, (string PSchema, string PTable, string Delete, List<string> Cols, List<string> PCols)>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var fkName   = reader.GetString(0);
            var tableKey = TableKey(reader.GetString(1), reader.GetString(2));

            if (!raw.TryGetValue(tableKey, out var fks))
                raw[tableKey] = fks = new Dictionary<string, (string, string, string, List<string>, List<string>)>(StringComparer.OrdinalIgnoreCase);

            if (!fks.TryGetValue(fkName, out var fkData))
            {
                fkData = (reader.GetString(5), reader.GetString(6), NormalizeDeleteAction(reader.GetString(8)), [], []);
                fks[fkName] = fkData;
            }

            fkData.Cols.Add(reader.GetString(3));
            fkData.PCols.Add(reader.GetString(7));
        }

        return raw.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                       .Select(fk => new ForeignKeyDefinition
                       {
                           Name            = fk.Key,
                           Columns         = fk.Value.Cols,
                           PrincipalSchema = fk.Value.PSchema,
                           PrincipalTable  = fk.Value.PTable,
                           PrincipalColumns = fk.Value.PCols,
                           DeleteBehavior  = fk.Value.Delete,
                       })
                       .ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeDeleteAction(string action) => action switch
    {
        "NO_ACTION"   => "NoAction",
        "CASCADE"     => "Cascade",
        "SET_NULL"    => "SetNull",
        "SET_DEFAULT" => "SetDefault",
        _             => action
    };

    private static async Task<Dictionary<string, List<IndexDefinition>>> ReadIndexesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                i.name                    AS IndexName,
                SCHEMA_NAME(t.schema_id)  AS TableSchema,
                t.name                    AS TableName,
                c.name                    AS ColumnName,
                i.is_unique               AS IsUnique,
                i.type                    AS IndexType,
                i.filter_definition       AS FilterDefinition
            FROM sys.indexes i
            INNER JOIN sys.tables t        ON i.object_id  = t.object_id
            INNER JOIN sys.index_columns ic ON i.object_id  = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c       ON ic.object_id = c.object_id  AND ic.column_id = c.column_id
            WHERE i.type > 0
              AND i.is_primary_key       = 0
              AND i.is_unique_constraint = 0
              AND ic.is_included_column  = 0
              AND i.name IS NOT NULL
            ORDER BY i.name, ic.key_ordinal
            """;

        // tableKey → indexName → (isUnique, isClustered, filter, columns)
        var raw = new Dictionary<string, Dictionary<string, (bool IsUnique, bool IsClustered, string? Filter, List<string> Cols)>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd    = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var indexName = reader.GetString(0);
            var tableKey  = TableKey(reader.GetString(1), reader.GetString(2));

            if (!raw.TryGetValue(tableKey, out var indexes))
                raw[tableKey] = indexes = new Dictionary<string, (bool, bool, string?, List<string>)>(StringComparer.OrdinalIgnoreCase);

            if (!indexes.TryGetValue(indexName, out var idxData))
            {
                idxData = (reader.GetBoolean(4), reader.GetByte(5) == 1, reader.IsDBNull(6) ? null : reader.GetString(6), []);
                indexes[indexName] = idxData;
            }

            idxData.Cols.Add(reader.GetString(3));
        }

        return raw.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                       .Select(idx => new IndexDefinition
                       {
                           Name       = idx.Key,
                           Columns    = idx.Value.Cols,
                           IsUnique   = idx.Value.IsUnique,
                           IsClustered = idx.Value.IsClustered,
                           Filter     = idx.Value.Filter,
                       })
                       .ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<HashSet<string>> ReadTemporalTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT SCHEMA_NAME(schema_id) AS TableSchema, name AS TableName
            FROM sys.tables
            WHERE temporal_type IN (1, 2)
            """;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd    = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                result.Add(TableKey(reader.GetString(0), reader.GetString(1)));
        }
        catch (SqlException)
        {
            // temporal_type column not available on SQL Server < 2016
        }

        return result;
    }

    private static async Task<Dictionary<string, (string? Start, string? End)>> ReadPeriodColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                SCHEMA_NAME(t.schema_id)  AS TableSchema,
                t.name                    AS TableName,
                cs.name                   AS StartColumn,
                ce.name                   AS EndColumn
            FROM sys.periods p
            INNER JOIN sys.tables t  ON p.object_id = t.object_id
            INNER JOIN sys.columns cs ON p.object_id = cs.object_id AND p.start_column_id = cs.column_id
            INNER JOIN sys.columns ce ON p.object_id = ce.object_id AND p.end_column_id   = ce.column_id
            WHERE p.period_type = 1
            """;

        var result = new Dictionary<string, (string? Start, string? End)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd    = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
                result[TableKey(reader.GetString(0), reader.GetString(1))] = (reader.GetString(2), reader.GetString(3));
        }
        catch (SqlException)
        {
            // sys.periods not available on SQL Server < 2016
        }

        return result;
    }
}
