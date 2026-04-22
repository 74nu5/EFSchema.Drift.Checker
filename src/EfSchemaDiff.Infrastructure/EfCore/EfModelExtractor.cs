using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Infrastructure.EfCore;

using System.Reflection;

/// <summary>
/// Extracts a <see cref="DatabaseSchema"/> from an EF Core <see cref="IModel"/> or <see cref="DbContext"/>.
/// Handles TPH/TPT/TPC hierarchies and owned types by merging all entity types that share the
/// same physical table into a single <see cref="TableDefinition"/>.
/// </summary>
public sealed class EfModelExtractor : IEfModelExtractor
{
    public DatabaseSchema Extract(DbContext dbContext) => Extract(dbContext.Model);

    public DatabaseSchema Extract(IModel model)
    {
        var accumulators = new Dictionary<(string Name, string? Schema), TableAccumulator>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            var viewName = entityType.GetViewName();

            // Skip unmapped entity types.
            if (tableName is null && viewName is null)
                continue;

            // When both are present, prefer the table mapping.
            var isView = tableName is null;
            var objectName = tableName ?? viewName!;
            var schema = isView ? entityType.GetViewSchema() : entityType.GetSchema();

            var storeObject = isView
                ? StoreObjectIdentifier.View(objectName, schema)
                : StoreObjectIdentifier.Table(objectName, schema);

            var tableKey = (objectName, schema);

            if (!accumulators.TryGetValue(tableKey, out var acc))
            {
                acc = new TableAccumulator
                {
                    Name = objectName,
                    Schema = schema,
                    IsView = isView,
                    IsOwned = entityType.IsOwned(),
                    IsKeyless = entityType.FindPrimaryKey() is null,
                    IsTemporal = IsTemporalEntity(entityType),
                    IsImplicitJoinTable = IsImplicitlyCreatedJoinTable(entityType),
                };
                accumulators[tableKey] = acc;
            }

            // Primary key — first entity type that declares one wins.
            if (!acc.PrimaryKeySet)
            {
                var pk = entityType.FindPrimaryKey();
                if (pk is not null)
                {
                    var pkCols = pk.Properties
                        .Select(p => p.GetColumnName(storeObject))
                        .OfType<string>()
                        .ToList();

                    if (pkCols.Count > 0)
                    {
                        acc.PrimaryKeyColumns = pkCols;
                        acc.PrimaryKeySet = true;
                    }
                }
            }

            // Temporal period property names declared on this entity type.
            var periodStartPropName = GetPeriodStartPropertyName(entityType);
            var periodEndPropName = GetPeriodEndPropertyName(entityType);

            // ── Columns ────────────────────────────────────────────────────────────
            foreach (var prop in entityType.GetProperties())
            {
                var colName = prop.GetColumnName(storeObject);
                if (colName is null || acc.Columns.ContainsKey(colName))
                    continue;

                var storeType = prop.GetColumnType();
                var computedSql = prop.GetComputedColumnSql();

                acc.Columns[colName] = new ColumnDefinition
                {
                    Name = colName,
                    StoreType = storeType,
                    IsNullable = prop.IsNullable,
                    MaxLength = prop.GetMaxLength(),
                    Precision = prop.GetPrecision(),
                    Scale = prop.GetScale(),
                    IsComputed = computedSql is not null || prop.ValueGenerated == ValueGenerated.OnAddOrUpdate,
                    ComputedColumnSql = computedSql,
                    IsRowVersion = prop.IsConcurrencyToken
                        && (storeType?.Contains("rowversion", StringComparison.OrdinalIgnoreCase) == true
                            || storeType?.Contains("timestamp", StringComparison.OrdinalIgnoreCase) == true),
                    IsShadow = prop.IsShadowProperty(),
                    DefaultValueSql = prop.GetDefaultValueSql(),
                    IsJson = IsJsonStoreType(storeType),
                    IsPeriodStart = periodStartPropName is not null && prop.Name == periodStartPropName,
                    IsPeriodEnd = periodEndPropName is not null && prop.Name == periodEndPropName,
                };
            }

            // ── Foreign keys ───────────────────────────────────────────────────────
            foreach (var fk in entityType.GetForeignKeys())
            {
                var principalTableName = fk.PrincipalEntityType.GetTableName()
                    ?? fk.PrincipalEntityType.GetViewName();
                if (principalTableName is null)
                    continue;

                var principalSchema = fk.PrincipalEntityType.GetSchema()
                    ?? fk.PrincipalEntityType.GetViewSchema();
                var principalStore = StoreObjectIdentifier.Table(principalTableName, principalSchema);

                var fkCols = fk.Properties
                    .Select(p => p.GetColumnName(storeObject))
                    .OfType<string>()
                    .ToList();
                if (fkCols.Count == 0) continue;

                string? fkName = null;
                try { fkName = fk.GetConstraintName(storeObject, principalStore); } catch { /* degraded */ }
                fkName ??= fk.GetConstraintName();
                if (fkName is null) continue;

                if (acc.ForeignKeys.ContainsKey(fkName)) continue;

                var principalCols = fk.PrincipalKey.Properties
                    .Select(p => p.GetColumnName(principalStore))
                    .OfType<string>()
                    .ToList();

                acc.ForeignKeys[fkName] = new ForeignKeyDefinition
                {
                    Name = fkName,
                    Columns = fkCols,
                    PrincipalTable = principalTableName,
                    PrincipalSchema = principalSchema,
                    PrincipalColumns = principalCols,
                    DeleteBehavior = fk.DeleteBehavior.ToString(),
                };
            }

            // ── Indexes ────────────────────────────────────────────────────────────
            foreach (var index in entityType.GetIndexes())
            {
                var idxCols = index.Properties
                    .Select(p => p.GetColumnName(storeObject))
                    .OfType<string>()
                    .ToList();
                if (idxCols.Count == 0) continue;

                string? idxName = null;
                try
                {
                    idxName = index.GetDatabaseName(storeObject)
                              ?? index.GetDefaultDatabaseName(storeObject);
                }
                catch { /* degraded */ }
                idxName ??= index.GetDatabaseName();
                if (string.IsNullOrEmpty(idxName)) continue;

                if (acc.Indexes.ContainsKey(idxName)) continue;

                // IsClustered is SQL Server-specific; read via annotation to avoid a direct package dep.
                var isClustered = index.FindAnnotation("SqlServer:Clustered")?.Value as bool? ?? false;

                acc.Indexes[idxName] = new IndexDefinition
                {
                    Name = idxName,
                    Columns = idxCols,
                    IsUnique = index.IsUnique,
                    IsClustered = isClustered,
                    Filter = index.GetFilter(),
                };
            }

            // ── Unique constraints (alternate keys, excl. PK) ─────────────────────
            var primaryKey = entityType.FindPrimaryKey();
            foreach (var altKey in entityType.GetKeys().Where(k => k != primaryKey))
            {
                var ucCols = altKey.Properties
                    .Select(p => p.GetColumnName(storeObject))
                    .OfType<string>()
                    .ToList();
                if (ucCols.Count == 0) continue;

                string? ucName = null;
                try
                {
                    ucName = altKey.GetName(storeObject)
                             ?? altKey.GetName()
                             ?? altKey.GetDefaultName();
                }
                catch { /* degraded */ }
                if (ucName is null) continue;

                if (acc.UniqueConstraints.ContainsKey(ucName)) continue;

                acc.UniqueConstraints[ucName] = new UniqueConstraintDefinition
                {
                    Name = ucName,
                    Columns = ucCols,
                };
            }
        }

        return new DatabaseSchema
        {
            Tables = [.. accumulators.Values.Select(a => a.Build())],
        };
    }

    // ── Temporal helpers ───────────────────────────────────────────────────────────

    private static bool IsTemporalEntity(IEntityType entityType)
    {
        if (entityType.IsMappedToJson()) return false;
        // Prefer the SqlServer:IsTemporal annotation; fall back to checking for a period-end property.
        return entityType.FindAnnotation("SqlServer:IsTemporal")?.Value as bool? == true
            || GetPeriodEndPropertyName(entityType) is not null;
    }

    /// <summary>
    /// Detects implicit many-to-many join tables created by EF Core convention.
    /// These use a shared Dictionary CLR type and carry a specific core annotation.
    /// </summary>
    private static bool IsImplicitlyCreatedJoinTable(IReadOnlyEntityType entityType)
    {
        if (!entityType.HasSharedClrType) return false;
        if (entityType.ClrType != typeof(Dictionary<string, object>)) return false;

        // EF Core sets "CoreAnnotations:ImplicitlyCreatedJoinEntityType" = true on these tables.
        return entityType.GetAnnotations()
            .Any(a => a.Name.EndsWith("ImplicitlyCreatedJoinEntityType", StringComparison.Ordinal)
                      && a.Value is true);
    }

    private static string? GetPeriodStartPropertyName(IEntityType entityType)
        => entityType.FindAnnotation("SqlServer:TemporalPeriodStartPropertyName")?.Value as string;

    private static string? GetPeriodEndPropertyName(IEntityType entityType)
        => entityType.FindAnnotation("SqlServer:TemporalPeriodEndPropertyName")?.Value as string;

    private static bool IsJsonStoreType(string? storeType)
        => storeType is not null
           && (storeType.Equals("json", StringComparison.OrdinalIgnoreCase)
               || storeType.Equals("jsonb", StringComparison.OrdinalIgnoreCase));

    // ── Inner accumulator ──────────────────────────────────────────────────────────

    private sealed class TableAccumulator
    {
        public required string Name { get; init; }
        public string? Schema { get; init; }
        public bool IsView { get; init; }
        public bool IsKeyless { get; init; }
        public bool IsTemporal { get; init; }
        public bool IsImplicitJoinTable { get; init; }

        public bool PrimaryKeySet { get; set; }
        public List<string> PrimaryKeyColumns { get; set; } = [];

        public Dictionary<string, ColumnDefinition> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ForeignKeyDefinition> ForeignKeys { get; } = [];
        public Dictionary<string, IndexDefinition> Indexes { get; } = [];
        public Dictionary<string, UniqueConstraintDefinition> UniqueConstraints { get; } = [];

        public bool IsOwned { get; set; }

        public TableDefinition Build() => new()
        {
            Name = Name,
            Schema = Schema,
            IsView = IsView,
            IsKeyless = IsKeyless,
            IsTemporal = IsTemporal,
            IsImplicitJoinTable = IsImplicitJoinTable,
            Columns = [.. Columns.Values],
            PrimaryKeyColumns = PrimaryKeyColumns,
            ForeignKeys = [.. ForeignKeys.Values],
            Indexes = [.. Indexes.Values],
            UniqueConstraints = [.. UniqueConstraints.Values],
            IsOwned = IsOwned,
        };
    }
}
