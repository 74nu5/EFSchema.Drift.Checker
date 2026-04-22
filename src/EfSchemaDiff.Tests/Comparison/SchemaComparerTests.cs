using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;
using EfSchemaDiff.Infrastructure.Comparison;
using EfSchemaDiff.Infrastructure.Providers.SqlServer;

namespace EfSchemaDiff.Tests.Comparison;

/// <summary>
/// Unit tests for <see cref="SchemaComparer"/>, covering all 14 implemented DiffTypes
/// and the filtering options.
/// </summary>
public sealed class SchemaComparerTests
{
    private static readonly SchemaCompareOptions DefaultOptions = new();

    private readonly SchemaComparer _sut = new(new SqlServerStoreTypeNormalizer());

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static DatabaseSchema Schema(params TableDefinition[] tables)
        => new() { Tables = [..tables] };

    private static TableDefinition Table(string name, string schema = "dbo",
        ColumnDefinition[]? columns = null,
        string[]? pkColumns = null,
        ForeignKeyDefinition[]? foreignKeys = null,
        IndexDefinition[]? indexes = null,
        UniqueConstraintDefinition[]? uniqueConstraints = null,
        bool isKeyless = false,
        bool isImplicitJoinTable = false)
        => new()
        {
            Name = name,
            Schema = schema,
            Columns = columns ?? [Col("Id", "int")],
            PrimaryKeyColumns = pkColumns ?? ["Id"],
            ForeignKeys = foreignKeys ?? [],
            Indexes = indexes ?? [],
            UniqueConstraints = uniqueConstraints ?? [],
            IsKeyless = isKeyless,
            IsImplicitJoinTable = isImplicitJoinTable,
        };

    private static ColumnDefinition Col(
        string name, string storeType,
        bool nullable = false,
        int? maxLength = null,
        int? precision = null,
        int? scale = null,
        bool isComputed = false)
        => new()
        {
            Name = name,
            StoreType = storeType,
            IsNullable = nullable,
            MaxLength = maxLength,
            Precision = precision,
            Scale = scale,
            IsComputed = isComputed,
        };

    // -----------------------------------------------------------------------
    // 1. TableMissingInDatabase
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_TableMissingInDatabase_ReportsError()
    {
        var ef = Schema(Table("Orders"));
        var db = Schema();

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.TableMissingInDatabase &&
                d.Severity == DiffSeverity.Error &&
                d.TableName == "Orders");
    }

    // -----------------------------------------------------------------------
    // 2. TableMissingInModel
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_TableMissingInModel_ReportsWarning()
    {
        var ef = Schema();
        var db = Schema(Table("Orphan"));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.TableMissingInModel &&
                d.Severity == DiffSeverity.Warning &&
                d.TableName == "Orphan");
    }

    // -----------------------------------------------------------------------
    // 3. ColumnMissingInDatabase
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_ColumnMissingInDatabase_ReportsError()
    {
        var ef = Schema(Table("Users", columns: [Col("Id", "int"), Col("Email", "nvarchar(200)")]));
        var db = Schema(Table("Users", columns: [Col("Id", "int")]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.ColumnMissingInDatabase &&
                d.ColumnName == "Email" &&
                d.Severity == DiffSeverity.Error);
    }

    // -----------------------------------------------------------------------
    // 4. ColumnMissingInModel
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_ColumnMissingInModel_ReportsWarning()
    {
        var ef = Schema(Table("Users", columns: [Col("Id", "int")]));
        var db = Schema(Table("Users", columns: [Col("Id", "int"), Col("LegacyCol", "nvarchar(50)")]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.ColumnMissingInModel &&
                d.ColumnName == "LegacyCol" &&
                d.Severity == DiffSeverity.Warning);
    }

    // -----------------------------------------------------------------------
    // 5. ColumnTypeMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_ColumnTypeMismatch_ReportsError()
    {
        var ef = Schema(Table("Users", columns: [Col("Id", "int"), Col("Name", "nvarchar(100)")]));
        var db = Schema(Table("Users", columns: [Col("Id", "int"), Col("Name", "varchar(100)")]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.ColumnTypeMismatch &&
                d.ColumnName == "Name" &&
                d.ExpectedValue == "nvarchar(100)" &&
                d.ActualValue == "varchar(100)");
    }

    [Fact]
    public void Compare_ColumnTypeMismatch_IgnoresComputedColumns()
    {
        var ef = Schema(Table("Products", columns: [Col("Id", "int"), Col("Price", "decimal(18,2)", isComputed: true)]));
        var db = Schema(Table("Products", columns: [Col("Id", "int"), Col("Price", "money")]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().NotContain(d => d.Type == DiffType.ColumnTypeMismatch);
    }

    [Fact]
    public void Compare_ColumnType_AliasNormalization_DetectsNoFalsePositive()
    {
        // "integer" and "int" should normalize to the same type → no diff
        var ef = Schema(Table("T", columns: [Col("Id", "integer")]));
        var db = Schema(Table("T", columns: [Col("Id", "int")]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().NotContain(d => d.Type == DiffType.ColumnTypeMismatch);
    }

    // -----------------------------------------------------------------------
    // 6. NullabilityMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_NullabilityMismatch_ReportsError()
    {
        var ef = Schema(Table("Orders", columns: [Col("Id", "int"), Col("Note", "nvarchar(500)", nullable: true)]));
        var db = Schema(Table("Orders", columns: [Col("Id", "int"), Col("Note", "nvarchar(500)", nullable: false)]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.NullabilityMismatch &&
                d.ColumnName == "Note");
    }

    // -----------------------------------------------------------------------
    // 7. MaxLengthMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_MaxLengthMismatch_ReportsWarning()
    {
        // Same base type — only metadata differs to avoid triggering ColumnTypeMismatch too
        var ef = Schema(Table("T", columns: [Col("Id", "int"), Col("Name", "nvarchar", maxLength: 100)]));
        var db = Schema(Table("T", columns: [Col("Id", "int"), Col("Name", "nvarchar", maxLength: 200)]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.MaxLengthMismatch &&
                d.Severity == DiffSeverity.Warning);
    }

    // -----------------------------------------------------------------------
    // 8. PrecisionMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_PrecisionMismatch_ReportsWarning()
    {
        var ef = Schema(Table("T", columns: [Col("Id", "int"), Col("Price", "decimal", precision: 18, scale: 2)]));
        var db = Schema(Table("T", columns: [Col("Id", "int"), Col("Price", "decimal", precision: 10, scale: 2)]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.PrecisionMismatch &&
                d.ExpectedValue == "18" &&
                d.ActualValue == "10");
    }

    // -----------------------------------------------------------------------
    // 9. ScaleMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_ScaleMismatch_ReportsWarning()
    {
        var ef = Schema(Table("T", columns: [Col("Id", "int"), Col("Rate", "decimal", precision: 18, scale: 4)]));
        var db = Schema(Table("T", columns: [Col("Id", "int"), Col("Rate", "decimal", precision: 18, scale: 2)]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.ScaleMismatch &&
                d.ExpectedValue == "4" &&
                d.ActualValue == "2");
    }

    // -----------------------------------------------------------------------
    // 10. PrimaryKeyMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_PrimaryKeyMismatch_ReportsError()
    {
        var ef = Schema(Table("Orders", pkColumns: ["Id"]));
        var db = Schema(Table("Orders", pkColumns: ["Id", "OrderDate"]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.PrimaryKeyMismatch &&
                d.Severity == DiffSeverity.Error);
    }

    [Fact]
    public void Compare_PrimaryKey_NoMismatch_WhenSame()
    {
        var ef = Schema(Table("Orders", pkColumns: ["Id"]));
        var db = Schema(Table("Orders", pkColumns: ["Id"]));

        _sut.Compare(ef, db, DefaultOptions).Differences.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // 11. ForeignKeyMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_ForeignKey_MissingInDb_ReportsWarning()
    {
        var fk = new ForeignKeyDefinition { Name = "FK_Orders_CustomerId", PrincipalTable = "Customers", Columns = ["CustomerId"], PrincipalColumns = ["Id"] };
        var ef = Schema(Table("Orders", foreignKeys: [fk]));
        var db = Schema(Table("Orders"));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.ForeignKeyMismatch &&
                d.ConstraintName == "FK_Orders_CustomerId" &&
                d.Severity == DiffSeverity.Warning);
    }

    // -----------------------------------------------------------------------
    // 12. IndexMissingInDatabase
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_IndexMissingInDatabase_ReportsWarning()
    {
        var idx = new IndexDefinition { Name = "IX_Orders_CustomerId", Columns = ["CustomerId"] };
        var ef = Schema(Table("Orders", indexes: [idx]));
        var db = Schema(Table("Orders"));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.IndexMissingInDatabase &&
                d.IndexName == "IX_Orders_CustomerId" &&
                d.Severity == DiffSeverity.Warning);
    }

    // -----------------------------------------------------------------------
    // 13. IndexMissingInModel
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_IndexMissingInModel_ReportsInfo()
    {
        var idx = new IndexDefinition { Name = "IX_Orphan", Columns = ["CustomerId"] };
        var ef = Schema(Table("Orders"));
        var db = Schema(Table("Orders", indexes: [idx]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.IndexMissingInModel &&
                d.Severity == DiffSeverity.Info);
    }

    // -----------------------------------------------------------------------
    // 14. UniqueConstraintMismatch
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_UniqueConstraintMismatch_ReportsWarning()
    {
        var uc = new UniqueConstraintDefinition { Name = "UQ_Users_Email", Columns = ["Email"] };
        var ef = Schema(Table("Users", uniqueConstraints: [uc]));
        var db = Schema(Table("Users"));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().ContainSingle()
            .Which.Should().Match<SchemaDifference>(d =>
                d.Type == DiffType.UniqueConstraintMismatch &&
                d.ConstraintName == "UQ_Users_Email" &&
                d.Severity == DiffSeverity.Warning);
    }

    // -----------------------------------------------------------------------
    // Filtering — schema
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_SchemaFilter_ExcludesOtherSchemas()
    {
        var ef = Schema(Table("T", schema: "billing"), Table("T2", schema: "core"));
        var db = Schema(Table("T", schema: "billing"));

        var result = _sut.Compare(ef, db, new SchemaCompareOptions { Schema = "billing" });

        // T2 is in a different schema and should be ignored even though it's missing in db
        result.Differences.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Filtering — IgnoreTables (glob)
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_IgnoreTables_GlobMatch_ExcludesTable()
    {
        // TableGlobKey returns "schema.table" when schema is set,
        // so the pattern must include the schema prefix or use a wildcard.
        var ef = Schema(Table("__EFMigrationsHistory"), Table("Orders"));
        var db = Schema(Table("Orders"));

        var result = _sut.Compare(ef, db, new SchemaCompareOptions
        {
            IgnoreTables = ["*.__EFMigrationsHistory"]
        });

        result.Differences.Should().BeEmpty();
    }

    [Fact]
    public void Compare_IgnoreTables_WildcardGlob_ExcludesMatchingTables()
    {
        var ef = Schema(Table("HangfireJob"), Table("HangfireState"), Table("Orders"));
        var db = Schema(Table("Orders"));

        var result = _sut.Compare(ef, db, new SchemaCompareOptions
        {
            IgnoreTables = ["*Hangfire*"]
        });

        result.Differences.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Filtering — ExcludeNavigationTables
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_ExcludeNavigationTables_SkipsImplicitJoinTables()
    {
        var ef = Schema(
            Table("TagPost", isImplicitJoinTable: true),
            Table("Posts"));
        var db = Schema(Table("Posts"));

        var result = _sut.Compare(ef, db, new SchemaCompareOptions
        {
            ExcludeNavigationTables = true
        });

        result.Differences.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Filtering — IgnoreColumns (glob)
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_IgnoreColumns_GlobMatch_ExcludesColumn()
    {
        var ef = Schema(Table("Users", columns: [Col("Id", "int"), Col("CreatedAt", "datetime2")]));
        var db = Schema(Table("Users", columns: [Col("Id", "int")]));

        var result = _sut.Compare(ef, db, new SchemaCompareOptions
        {
            IgnoreColumns = ["*.CreatedAt"]
        });

        result.Differences.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Period columns — excluded from comparison
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_PeriodColumns_AreExcludedFromComparison()
    {
        var efPeriodStart = new ColumnDefinition { Name = "ValidFrom", StoreType = "datetime2", IsPeriodStart = true };
        var ef = Schema(Table("T", columns: [Col("Id", "int"), efPeriodStart]));
        var db = Schema(Table("T", columns: [Col("Id", "int")]));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().NotContain(d => d.ColumnName == "ValidFrom");
    }

    // -----------------------------------------------------------------------
    // No differences — clean result
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_IdenticalSchemas_ReturnsNoDifferences()
    {
        var schema = Schema(
            Table("Orders", columns: [Col("Id", "int"), Col("Total", "decimal(18,2)", precision: 18, scale: 2)]),
            Table("Customers", columns: [Col("Id", "int"), Col("Name", "nvarchar(200)", maxLength: 200)]));

        var result = _sut.Compare(schema, schema, DefaultOptions);

        result.HasDifferences.Should().BeFalse();
        result.Differences.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Keyless entities — skip PK/FK/index comparisons
    // -----------------------------------------------------------------------

    [Fact]
    public void Compare_KeylessTable_SkipsPrimaryKeyComparison()
    {
        var ef = Schema(Table("v_ActiveOrders", pkColumns: [], isKeyless: true));
        var db = Schema(Table("v_ActiveOrders", pkColumns: []));

        var result = _sut.Compare(ef, db, DefaultOptions);

        result.Differences.Should().NotContain(d => d.Type == DiffType.PrimaryKeyMismatch);
    }
}
