using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;
using Spectre.Console;

namespace EfSchemaDiff.Cli.Formatters;

/// <summary>Renders the diff result as a Spectre.Console table to stdout.</summary>
public sealed class ConsoleOutputFormatter : IOutputFormatter
{
    public string FormatName => "table";

    private readonly bool _noColor;

    public ConsoleOutputFormatter(bool noColor = false)
    {
        _noColor = noColor;
    }

    public string Format(SchemaDiffResult result)
    {
        // This formatter writes directly to the console via Spectre.Console
        // and returns an empty string (side-effect formatter).
        Render(result);
        return string.Empty;
    }

    public void Render(SchemaDiffResult result)
    {
        var console = _noColor ? AnsiConsole.Create(new AnsiConsoleSettings
        {
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(System.Console.Out)
        }) : AnsiConsole.Console;

        if (!result.HasDifferences)
        {
            console.MarkupLine("[green]✔ No schema differences found[/]");
            return;
        }

        console.Write(new Rule("[bold]Differences[/]").RuleStyle(Style.Parse("yellow dim")));

        var table = new Table();
        table.AddColumn(new TableColumn("[bold]Type[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Object[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Details[/]").LeftAligned());

        foreach (var diff in result.Differences.OrderBy(d => d.Severity).ThenBy(d => d.Type))
        {
            var typeCell = FormatDiffType(diff.Type, diff.Severity);
            var objectCell = Markup.Escape(diff.ObjectName);
            var detailsCell = BuildDetailsCell(diff);

            table.AddRow(typeCell, objectCell, detailsCell);
        }

        console.Write(table);
        console.WriteLine();

        var parts = new List<string>();
        if (result.ErrorCount   > 0) parts.Add($"[red]{result.ErrorCount} {(result.ErrorCount == 1 ? "error" : "errors")}[/]");
        if (result.WarningCount > 0) parts.Add($"[yellow]{result.WarningCount} {(result.WarningCount == 1 ? "warning" : "warnings")}[/]");
        if (result.InfoCount    > 0) parts.Add($"[blue]{result.InfoCount} info[/]");

        var icon = result.ErrorCount > 0 ? "[red]✘[/]" : "[yellow]⚠[/]";
        var detail = parts.Count > 0 ? "  [dim]·[/]  " + string.Join("  [dim]·[/]  ", parts) : string.Empty;
        console.MarkupLine($"{icon} {result.DifferenceCount} {(result.DifferenceCount == 1 ? "difference" : "differences")}{detail}");
    }

    private static string FormatDiffType(DiffType type, DiffSeverity severity)
    {
        var color = severity switch
        {
            DiffSeverity.Error => "red",
            DiffSeverity.Warning => "yellow",
            DiffSeverity.Info => "blue",
            _ => "white"
        };
        return $"[{color}]{Markup.Escape(type.ToString())}[/]";
    }

    private static string BuildDetailsCell(SchemaDifference diff)
    {
        if (diff.ExpectedValue is not null && diff.ActualValue is not null)
            return $"EF: [cyan]{Markup.Escape(diff.ExpectedValue)}[/] → DB: [yellow]{Markup.Escape(diff.ActualValue)}[/]";
        return Markup.Escape(diff.Details);
    }
}
