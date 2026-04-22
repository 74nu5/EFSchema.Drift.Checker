using System.Text;
using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Cli.Formatters;

public sealed class MarkdownOutputFormatter : IOutputFormatter
{
    public string FormatName => "markdown";

    public string Format(SchemaDiffResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EF Schema Diff Report");
        sb.AppendLine();

        if (!result.HasDifferences)
        {
            sb.AppendLine("✅ **No schema differences found.**");
            return sb.ToString();
        }

        sb.AppendLine($"## Summary");
        sb.AppendLine();
        sb.AppendLine($"| | Count |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| **Total differences** | {result.DifferenceCount} |");
        sb.AppendLine($"| 🔴 Errors | {result.ErrorCount} |");
        sb.AppendLine($"| 🟡 Warnings | {result.WarningCount} |");
        sb.AppendLine($"| 🔵 Info | {result.InfoCount} |");
        sb.AppendLine();

        sb.AppendLine($"## Differences");
        sb.AppendLine();
        sb.AppendLine("| Severity | Type | Object | Details |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var diff in result.Differences.OrderBy(d => d.Severity).ThenBy(d => d.Type))
        {
            var severityIcon = diff.Severity switch
            {
                DiffSeverity.Error => "🔴",
                DiffSeverity.Warning => "🟡",
                DiffSeverity.Info => "🔵",
                _ => ""
            };

            var details = string.IsNullOrEmpty(diff.ExpectedValue) || string.IsNullOrEmpty(diff.ActualValue)
                ? EscapeMarkdown(diff.Details)
                : $"EF: `{EscapeMarkdown(diff.ExpectedValue)}` → DB: `{EscapeMarkdown(diff.ActualValue)}`";

            sb.AppendLine($"| {severityIcon} {diff.Severity} | {diff.Type} | `{EscapeMarkdown(diff.ObjectName)}` | {details} |");
        }

        return sb.ToString();
    }

    private static string EscapeMarkdown(string? value) =>
        value?.Replace("|", "\\|").Replace("`", "'") ?? string.Empty;
}
