using System.Text.Json;
using System.Text.Json.Serialization;
using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Cli.Formatters;

public sealed class JsonOutputFormatter : IOutputFormatter
{
    public string FormatName => "json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Format(SchemaDiffResult result)
    {
        var payload = new
        {
            hasDifferences = result.HasDifferences,
            differenceCount = result.DifferenceCount,
            errorCount = result.ErrorCount,
            warningCount = result.WarningCount,
            infoCount = result.InfoCount,
            differences = result.Differences.Select(d => new
            {
                type = d.Type.ToString(),
                severity = d.Severity.ToString(),
                objectName = d.ObjectName,
                details = d.Details,
                expectedValue = d.ExpectedValue,
                actualValue = d.ActualValue,
                location = new
                {
                    schema = d.SchemaName,
                    table = d.TableName,
                    column = d.ColumnName,
                    constraint = d.ConstraintName,
                    index = d.IndexName
                }
            }).ToList()
        };

        return JsonSerializer.Serialize(payload, Options);
    }
}
