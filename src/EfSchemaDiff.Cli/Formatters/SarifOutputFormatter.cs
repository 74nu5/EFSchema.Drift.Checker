using System.Text;
using System.Text.Json;
using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Cli.Formatters;

/// <summary>Produces a SARIF 2.1.0 report for integration with GitHub Code Scanning, Azure DevOps, SonarQube.</summary>
public sealed class SarifOutputFormatter : IOutputFormatter
{
    private const string SarifVersion = "2.1.0";
    private const string SarifSchema = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";
    private const string ToolName = "ef-schema-diff";
    private const string ToolVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FormatName => "sarif";

    public string Format(SchemaDiffResult result)
    {
        var rules = result.Differences
            .Select(d => d.Type.ToString())
            .Distinct()
            .Select(ruleId => new
            {
                id = ruleId,
                name = ruleId,
                shortDescription = new { text = FormatRuleDescription(ruleId) },
                defaultConfiguration = new { level = "error" }
            })
            .ToList();

        var results = result.Differences.Select(d => new
        {
            ruleId = d.Type.ToString(),
            level = MapSeverity(d.Severity),
            message = new { text = BuildMessage(d) },
            locations = new[]
            {
                new
                {
                    logicalLocations = new[]
                    {
                        new
                        {
                            name = d.ObjectName,
                            kind = ResolveLocationKind(d),
                            fullyQualifiedName = BuildFullyQualifiedName(d)
                        }
                    }
                }
            },
            fingerprints = new Dictionary<string, string>
            {
                ["efSchemaDiff/v1"] = ComputeFingerprint(d)
            },
            properties = BuildProperties(d)
        }).ToList();

        var sarif = new
        {
            version = SarifVersion,
            schema = SarifSchema,
            runs = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = ToolName,
                            version = ToolVersion,
                            informationUri = "https://github.com/efschema/ef-schema-diff",
                            rules
                        }
                    },
                    results,
                    columnKind = "utf16CodeUnits"
                }
            }
        };

        return JsonSerializer.Serialize(sarif, JsonOptions);
    }

    private static string MapSeverity(DiffSeverity severity) => severity switch
    {
        DiffSeverity.Error => "error",
        DiffSeverity.Warning => "warning",
        DiffSeverity.Info => "note",
        _ => "none"
    };

    private static string ResolveLocationKind(SchemaDifference d)
    {
        if (d.ColumnName is not null) return "column";
        if (d.IndexName is not null) return "index";
        if (d.ConstraintName is not null) return "constraint";
        return "table";
    }

    private static string BuildFullyQualifiedName(SchemaDifference d)
    {
        var sb = new StringBuilder();
        if (d.SchemaName is not null) sb.Append(d.SchemaName).Append('.');
        if (d.TableName is not null) sb.Append(d.TableName);
        if (d.ColumnName is not null) sb.Append('.').Append(d.ColumnName);
        else if (d.IndexName is not null) sb.Append('#').Append(d.IndexName);
        else if (d.ConstraintName is not null) sb.Append('#').Append(d.ConstraintName);
        return sb.ToString();
    }

    private static string BuildMessage(SchemaDifference d)
    {
        if (d.ExpectedValue is not null && d.ActualValue is not null)
            return $"{d.Details} Expected: {d.ExpectedValue}, Actual: {d.ActualValue}";
        return d.Details;
    }

    private static Dictionary<string, object?> BuildProperties(SchemaDifference d)
    {
        var props = new Dictionary<string, object?>();
        if (d.SchemaName is not null) props["schemaName"] = d.SchemaName;
        if (d.TableName is not null) props["tableName"] = d.TableName;
        if (d.ColumnName is not null) props["columnName"] = d.ColumnName;
        if (d.ConstraintName is not null) props["constraintName"] = d.ConstraintName;
        if (d.IndexName is not null) props["indexName"] = d.IndexName;
        if (d.ExpectedValue is not null) props["expectedValue"] = d.ExpectedValue;
        if (d.ActualValue is not null) props["actualValue"] = d.ActualValue;
        return props;
    }

    private static string ComputeFingerprint(SchemaDifference d) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"{d.Type}:{d.ObjectName}")))
        [..16].ToLowerInvariant();

    private static string FormatRuleDescription(string ruleId) => ruleId switch
    {
        "TableMissingInDatabase" => "A table exists in the EF Core model but is absent from the database.",
        "TableMissingInModel" => "A table exists in the database but is absent from the EF Core model.",
        "ColumnMissingInDatabase" => "A column exists in the EF Core model but is absent from the database.",
        "ColumnMissingInModel" => "A column exists in the database but is absent from the EF Core model.",
        "ColumnTypeMismatch" => "The SQL type of a column differs between EF Core and the database.",
        "NullabilityMismatch" => "The nullability of a column differs between EF Core and the database.",
        "MaxLengthMismatch" => "The maximum length of a column differs between EF Core and the database.",
        "PrecisionMismatch" => "The precision of a column differs between EF Core and the database.",
        "ScaleMismatch" => "The scale of a column differs between EF Core and the database.",
        "PrimaryKeyMismatch" => "The primary key definition differs between EF Core and the database.",
        "ForeignKeyMismatch" => "A foreign key differs between EF Core and the database.",
        "IndexMissingInDatabase" => "An index exists in the EF Core model but is absent from the database.",
        "IndexMissingInModel" => "An index exists in the database but is absent from the EF Core model.",
        "UniqueConstraintMismatch" => "A unique constraint differs between EF Core and the database.",
        "SchemaMismatch" => "The SQL schema of a table differs between EF Core and the database.",
        _ => ruleId
    };
}
