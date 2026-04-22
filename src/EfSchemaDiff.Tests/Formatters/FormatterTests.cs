using System.Text.Json;
using EfSchemaDiff.Cli.Formatters;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Tests.Formatters;

public sealed class FormatterTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static SchemaDiffResult EmptyResult() => new() { Differences = [] };

    private static SchemaDiffResult ResultWithDifferences(params SchemaDifference[] diffs)
        => new() { Differences = [..diffs] };

    private static SchemaDifference TableMissing(string table) => new()
    {
        Type = DiffType.TableMissingInDatabase,
        Severity = DiffSeverity.Error,
        ObjectName = table,
        Details = $"Table '{table}' is missing in the database.",
        TableName = table,
        SchemaName = "dbo"
    };

    private static SchemaDifference ColumnType(string table, string column, string expected, string actual) => new()
    {
        Type = DiffType.ColumnTypeMismatch,
        Severity = DiffSeverity.Error,
        ObjectName = $"dbo.{table}.{column}",
        Details = $"Column '{column}' type mismatch.",
        ExpectedValue = expected,
        ActualValue = actual,
        SchemaName = "dbo",
        TableName = table,
        ColumnName = column
    };

    private static SchemaDifference IndexMissing(string table, string index) => new()
    {
        Type = DiffType.IndexMissingInDatabase,
        Severity = DiffSeverity.Warning,
        ObjectName = index,
        Details = $"Index '{index}' is missing in the database.",
        TableName = table,
        IndexName = index
    };

    // -----------------------------------------------------------------------
    // JSON formatter
    // -----------------------------------------------------------------------

    [Fact]
    public void JsonFormatter_EmptyResult_IsValidJson_WithNoDifferences()
    {
        var sut = new JsonOutputFormatter();
        var json = sut.Format(EmptyResult());

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("hasDifferences").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("differenceCount").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("differences").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void JsonFormatter_WithDifferences_ContainsExpectedFields()
    {
        var sut = new JsonOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"), ColumnType("Users", "Email", "nvarchar(200)", "varchar(200)"));
        var json = sut.Format(result);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("hasDifferences").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("differenceCount").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("errorCount").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("warningCount").GetInt32().Should().Be(0);

        var diffs = doc.RootElement.GetProperty("differences");
        diffs.GetArrayLength().Should().Be(2);

        var first = diffs[0];
        first.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("severity").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("objectName").GetString().Should().NotBeNullOrEmpty();
        first.TryGetProperty("location", out var location).Should().BeTrue();
        location.GetProperty("table").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void JsonFormatter_WithTypeMismatch_IncludesExpectedAndActualValues()
    {
        var sut = new JsonOutputFormatter();
        var result = ResultWithDifferences(ColumnType("Products", "Price", "decimal(18,2)", "money"));
        var json = sut.Format(result);

        var doc = JsonDocument.Parse(json);
        var diff = doc.RootElement.GetProperty("differences")[0];
        diff.GetProperty("expectedValue").GetString().Should().Be("decimal(18,2)");
        diff.GetProperty("actualValue").GetString().Should().Be("money");
    }

    [Fact]
    public void JsonFormatter_Location_HasColumnForColumnDiff()
    {
        var sut = new JsonOutputFormatter();
        var result = ResultWithDifferences(ColumnType("Users", "Email", "nvarchar(200)", "varchar(200)"));
        var json = sut.Format(result);

        var doc = JsonDocument.Parse(json);
        var location = doc.RootElement.GetProperty("differences")[0].GetProperty("location");
        location.GetProperty("column").GetString().Should().Be("Email");
    }

    [Fact]
    public void JsonFormatter_Location_HasIndexForIndexDiff()
    {
        var sut = new JsonOutputFormatter();
        var result = ResultWithDifferences(IndexMissing("Orders", "IX_Orders_CustomerId"));
        var json = sut.Format(result);

        var doc = JsonDocument.Parse(json);
        var location = doc.RootElement.GetProperty("differences")[0].GetProperty("location");
        location.GetProperty("index").GetString().Should().Be("IX_Orders_CustomerId");
    }

    [Fact]
    public void JsonFormatter_FormatName_IsJson()
        => new JsonOutputFormatter().FormatName.Should().Be("json");

    // -----------------------------------------------------------------------
    // Markdown formatter
    // -----------------------------------------------------------------------

    [Fact]
    public void MarkdownFormatter_EmptyResult_ContainsNoSchema()
    {
        var sut = new MarkdownOutputFormatter();
        var md = sut.Format(EmptyResult());

        md.Should().Contain("No schema differences found");
        md.Should().NotContain("|");
    }

    [Fact]
    public void MarkdownFormatter_WithDifferences_ContainsTableSyntax()
    {
        var sut = new MarkdownOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"));
        var md = sut.Format(result);

        md.Should().Contain("|");
        md.Should().Contain("Severity");
        md.Should().Contain("Type");
        md.Should().Contain("Object");
    }

    [Fact]
    public void MarkdownFormatter_WithDifferences_ContainsSeverityIcon()
    {
        var sut = new MarkdownOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"));
        var md = sut.Format(result);

        md.Should().Contain("🔴"); // Error icon
    }

    [Fact]
    public void MarkdownFormatter_WithTypeMismatch_ShowsExpectedAndActual()
    {
        var sut = new MarkdownOutputFormatter();
        var result = ResultWithDifferences(ColumnType("Users", "Email", "nvarchar(200)", "varchar(200)"));
        var md = sut.Format(result);

        md.Should().Contain("nvarchar(200)");
        md.Should().Contain("varchar(200)");
    }

    [Fact]
    public void MarkdownFormatter_ContainsSummarySection()
    {
        var sut = new MarkdownOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"));
        var md = sut.Format(result);

        md.Should().Contain("Summary");
        md.Should().Contain("Errors");
    }

    [Fact]
    public void MarkdownFormatter_FormatName_IsMarkdown()
        => new MarkdownOutputFormatter().FormatName.Should().Be("markdown");

    // -----------------------------------------------------------------------
    // SARIF formatter
    // -----------------------------------------------------------------------

    [Fact]
    public void SarifFormatter_EmptyResult_IsValidSarif()
    {
        var sut = new SarifOutputFormatter();
        var sarif = sut.Format(EmptyResult());

        var doc = JsonDocument.Parse(sarif);
        doc.RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
        doc.RootElement.TryGetProperty("runs", out _).Should().BeTrue();
    }

    [Fact]
    public void SarifFormatter_WithDifferences_ContainsResults()
    {
        var sut = new SarifOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"), ColumnType("Users", "Email", "nvarchar(200)", "varchar(200)"));
        var sarif = sut.Format(result);

        var doc = JsonDocument.Parse(sarif);
        var run = doc.RootElement.GetProperty("runs")[0];
        run.GetProperty("results").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void SarifFormatter_WithDifferences_HasRules()
    {
        var sut = new SarifOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"));
        var sarif = sut.Format(result);

        var doc = JsonDocument.Parse(sarif);
        var rules = doc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules");
        rules.GetArrayLength().Should().BeGreaterThan(0);
        rules[0].GetProperty("id").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SarifFormatter_Result_HasFingerprintAndRuleId()
    {
        var sut = new SarifOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders"));
        var sarif = sut.Format(result);

        var doc = JsonDocument.Parse(sarif);
        var r = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        r.GetProperty("ruleId").GetString().Should().NotBeNullOrEmpty();
        r.TryGetProperty("fingerprints", out var fps).Should().BeTrue();
        fps.TryGetProperty("efSchemaDiff/v1", out var fp).Should().BeTrue();
        fp.GetString()!.Length.Should().Be(16);
    }

    [Fact]
    public void SarifFormatter_SeverityMapping_ErrorMapsToError()
    {
        var sut = new SarifOutputFormatter();
        var result = ResultWithDifferences(TableMissing("Orders")); // Error severity
        var sarif = sut.Format(result);

        var doc = JsonDocument.Parse(sarif);
        var r = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        r.GetProperty("level").GetString().Should().Be("error");
    }

    [Fact]
    public void SarifFormatter_SeverityMapping_WarningMapsToWarning()
    {
        var sut = new SarifOutputFormatter();
        var result = ResultWithDifferences(IndexMissing("Orders", "IX_Orders_Cust")); // Warning severity
        var sarif = sut.Format(result);

        var doc = JsonDocument.Parse(sarif);
        var r = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        r.GetProperty("level").GetString().Should().Be("warning");
    }

    [Fact]
    public void SarifFormatter_FormatName_IsSarif()
        => new SarifOutputFormatter().FormatName.Should().Be("sarif");

    // -----------------------------------------------------------------------
    // Console formatter (side-effect, returns empty string)
    // -----------------------------------------------------------------------

    [Fact]
    public void ConsoleFormatter_Format_ReturnsEmptyString()
    {
        var sut = new ConsoleOutputFormatter(noColor: true);
        sut.Format(EmptyResult()).Should().BeEmpty();
    }

    [Fact]
    public void ConsoleFormatter_Format_WithDifferences_ReturnsEmptyString()
    {
        var sut = new ConsoleOutputFormatter(noColor: true);
        sut.Format(ResultWithDifferences(TableMissing("Orders"))).Should().BeEmpty();
    }

    [Fact]
    public void ConsoleFormatter_FormatName_IsTable()
        => new ConsoleOutputFormatter().FormatName.Should().Be("table");
}
