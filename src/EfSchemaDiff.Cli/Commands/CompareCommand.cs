using System.CommandLine;
using System.CommandLine.Invocation;
using EfSchemaDiff.Cli.Formatters;
using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;
using EfSchemaDiff.Infrastructure.Comparison;
using EfSchemaDiff.Infrastructure.EfCore;
using EfSchemaDiff.Infrastructure.Loading;
using EfSchemaDiff.Infrastructure.Providers.SqlServer;
using Spectre.Console;

namespace EfSchemaDiff.Cli.Commands;

/// <summary>
/// Builds and registers the <c>compare</c> command for <c>ef-schema-diff</c>.
/// </summary>
public static class CompareCommand
{
    // ---------------------------------------------------------------------------
    // Option declarations
    // ---------------------------------------------------------------------------

    private static readonly Option<string> AssemblyOption = new(
        aliases: ["--assembly", "-a"],
        description: "Path to the assembly (.dll) containing the DbContext.")
    { IsRequired = true };

    private static readonly Option<string?> ContextOption = new(
        aliases: ["--context", "-c"],
        description: "DbContext type name (simple or fully qualified). Required when multiple DbContext types exist.");

    private static readonly Option<string?> ConnectionOption = new(
        aliases: ["--connection", "-s"],
        description: "SQL Server connection string. Falls back to EFSD_CONNECTION environment variable if omitted.");

    private static readonly Option<string?> StartupAssemblyOption = new(
        "--startup-assembly",
        description: "Startup assembly path, used to resolve IHostBuilder (strategy 3).");

    private static readonly Option<string?> SchemaOption = new(
        "--schema",
        description: "Restrict comparison to a single database schema (e.g. 'dbo').");

    private static readonly Option<string[]> IgnoreTablesOption = new(
        "--ignore-tables",
        description: "Glob patterns of tables to exclude (e.g. '__EFMigrationsHistory', 'Hangfire.*'). Repeatable.")
    { AllowMultipleArgumentsPerToken = true };

    private static readonly Option<string[]> IgnoreColumnsOption = new(
        "--ignore-columns",
        description: "Glob patterns of columns to exclude (e.g. '*.CreatedAt'). Repeatable.")
    { AllowMultipleArgumentsPerToken = true };

    private static readonly Option<bool> ExcludeNavTablesOption = new(
        "--exclude-navigation-tables",
        description: "Skip auto-generated many-to-many join tables.",
        getDefaultValue: () => false);

    private static readonly Option<string> OutputOption = new(
        aliases: ["--output", "-o"],
        description: "Output format: table (default), json, markdown, sarif.",
        getDefaultValue: () => "table");

    private static readonly Option<string?> OutputFileOption = new(
        "--output-file",
        description: "Write formatted output to this file instead of stdout.");

    private static readonly Option<bool> NoColorOption = new(
        "--no-color",
        description: "Disable ANSI colour output (only affects 'table' format).",
        getDefaultValue: () => false);

    private static readonly Option<string> MinSeverityOption = new(
        "--min-severity",
        description: "Minimum severity to include in output and exit-code evaluation: Error, Warning (default), Info.",
        getDefaultValue: () => "Warning");

    // ---------------------------------------------------------------------------
    // Command factory
    // ---------------------------------------------------------------------------

    public static Command Build()
    {
        var command = new Command("compare", "Compare the EF Core model against the real SQL Server schema.");

        command.AddOption(AssemblyOption);
        command.AddOption(ContextOption);
        command.AddOption(ConnectionOption);
        command.AddOption(StartupAssemblyOption);
        command.AddOption(SchemaOption);
        command.AddOption(IgnoreTablesOption);
        command.AddOption(IgnoreColumnsOption);
        command.AddOption(ExcludeNavTablesOption);
        command.AddOption(OutputOption);
        command.AddOption(OutputFileOption);
        command.AddOption(NoColorOption);
        command.AddOption(MinSeverityOption);

        command.SetHandler(HandleAsync);
        return command;
    }

    // ---------------------------------------------------------------------------
    // Handler
    // ---------------------------------------------------------------------------

    private static async Task HandleAsync(InvocationContext ctx)
    {
        var pr = ctx.ParseResult;

        var noColor         = pr.GetValueForOption(NoColorOption);
        var assemblyPath    = pr.GetValueForOption(AssemblyOption)!;
        var contextName     = pr.GetValueForOption(ContextOption);
        var startupAssembly = pr.GetValueForOption(StartupAssemblyOption);
        var schema          = pr.GetValueForOption(SchemaOption);
        var ignoreTables    = pr.GetValueForOption(IgnoreTablesOption) ?? [];
        var ignoreColumns   = pr.GetValueForOption(IgnoreColumnsOption) ?? [];
        var excludeNav      = pr.GetValueForOption(ExcludeNavTablesOption);
        var outputFormat    = pr.GetValueForOption(OutputOption) ?? "table";
        var outputFile      = pr.GetValueForOption(OutputFileOption);
        var minSeverityRaw  = pr.GetValueForOption(MinSeverityOption) ?? "Warning";

        // Exit-code constants
        const int ExitOk           = 0;
        const int ExitDifferences  = 1;
        const int ExitBadConfig    = 2;
        const int ExitDbError      = 3;
        const int ExitContextError = 4;
        const int ExitUnexpected   = 5;

        // Progress console writes to stderr so that structured output (JSON/SARIF/…) on
        // stdout stays clean even when the user redirects stdout to a file.
        var progress = CreateProgressConsole(noColor);

        // -- Validate inputs before rendering the header --
        if (!File.Exists(assemblyPath))
        {
            progress.MarkupLine($"[red]✘[/] Assembly not found: [yellow]{Markup.Escape(assemblyPath)}[/]");
            ctx.ExitCode = ExitBadConfig;
            return;
        }

        if (!Enum.TryParse<DiffSeverity>(minSeverityRaw, ignoreCase: true, out var minSeverity))
        {
            progress.MarkupLine($"[red]✘[/] Unknown severity '[yellow]{Markup.Escape(minSeverityRaw)}[/]'. Valid values: Error, Warning, Info.");
            ctx.ExitCode = ExitBadConfig;
            return;
        }

        var connection = pr.GetValueForOption(ConnectionOption)
                         ?? Environment.GetEnvironmentVariable("EFSD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            progress.MarkupLine("[red]✘[/] Connection string is required. Use [dim]--connection[/] or set [dim]EFSD_CONNECTION[/].");
            ctx.ExitCode = ExitBadConfig;
            return;
        }

        var cancellationToken = ctx.GetCancellationToken();

        // -- Header --
        RenderHeader(progress, assemblyPath, contextName, connection);

        try
        {
            // ----------------------------------------------------------------
            // Step 1 — Load EF Core model
            // ----------------------------------------------------------------
            DatabaseSchema efSchema;
            try
            {
                efSchema = await progress.Status()
                    .Spinner(Spinner.Known.Dots2)
                    .SpinnerStyle(Style.Parse("blue bold"))
                    .StartAsync("Loading EF Core model...", async _ =>
                        await new DbContextLoader(new EfModelExtractor())
                            .LoadAndExtractAsync(assemblyPath, startupAssembly,
                                contextName ?? string.Empty, connection, cancellationToken));

                progress.MarkupLine($"[green]✔[/] EF Core model loaded    [dim]({efSchema.Tables.Count} {Pluralize(efSchema.Tables.Count, "entity", "entities")})[/]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.MarkupLine("[red]✘[/] Failed to load EF Core model");
                RenderException(progress, ex);
                ctx.ExitCode = ExitContextError;
                return;
            }

            // ----------------------------------------------------------------
            // Step 2 — Read SQL Server schema
            // ----------------------------------------------------------------
            DatabaseSchema dbSchema;
            try
            {
                dbSchema = await progress.Status()
                    .Spinner(Spinner.Known.Dots2)
                    .SpinnerStyle(Style.Parse("blue bold"))
                    .StartAsync("Reading SQL Server schema...", async _ =>
                        await new SqlServerSchemaReader().ReadAsync(connection, cancellationToken));

                progress.MarkupLine($"[green]✔[/] Database schema read     [dim]({dbSchema.Tables.Count} {Pluralize(dbSchema.Tables.Count, "table", "tables")})[/]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsDbException(ex))
            {
                progress.MarkupLine("[red]✘[/] Database connection failed");
                progress.MarkupLine($"  [red]{Markup.Escape(ex.Message)}[/]");
                ctx.ExitCode = ExitDbError;
                return;
            }

            // ----------------------------------------------------------------
            // Step 3 — Compare (fast, inline — no spinner needed)
            // ----------------------------------------------------------------
            var options = new SchemaCompareOptions
            {
                Schema = schema,
                IgnoreTables = ignoreTables,
                IgnoreColumns = ignoreColumns,
                ExcludeNavigationTables = excludeNav,
            };

            var diffResult = new SchemaComparer(new SqlServerStoreTypeNormalizer())
                .Compare(efSchema, dbSchema, options);

            var n = diffResult.DifferenceCount;
            if (n == 0)
                progress.MarkupLine("[green]✔[/] Comparison complete      [dim](no differences)[/]");
            else
                progress.MarkupLine($"[yellow]✔[/] Comparison complete      [dim]({n} {Pluralize(n, "difference", "differences")})[/]");

            progress.WriteLine();

            // ----------------------------------------------------------------
            // Filter + format + output
            // ----------------------------------------------------------------
            var filtered = new SchemaDiffResult
            {
                Differences = diffResult.Differences
                    .Where(d => d.Severity <= minSeverity)
                    .ToList()
            };

            IOutputFormatter formatter = outputFormat.ToLowerInvariant() switch
            {
                "json"     => new JsonOutputFormatter(),
                "markdown" => new MarkdownOutputFormatter(),
                "sarif"    => new SarifOutputFormatter(),
                _          => new ConsoleOutputFormatter(noColor),
            };

            var output = formatter.Format(filtered);

            if (!string.IsNullOrEmpty(outputFile))
            {
                await File.WriteAllTextAsync(outputFile, output, cancellationToken);
                progress.MarkupLine($"[green]✔[/] Output written to [dim]{Markup.Escape(outputFile)}[/]");
                if (outputFormat.ToLowerInvariant() != "table")
                    progress.MarkupLine(BuildSummaryMarkup(filtered));
            }
            else if (!string.IsNullOrEmpty(output))
            {
                Console.Write(output); // stdout — stays clean for piping/redirection
            }

            ctx.ExitCode = filtered.HasDifferences ? ExitDifferences : ExitOk;
        }
        catch (OperationCanceledException)
        {
            progress.MarkupLine("[yellow]⊘ Operation cancelled[/]");
            ctx.ExitCode = ExitUnexpected;
        }
        catch (Exception ex)
        {
            progress.MarkupLine("[red]⚡ Unexpected error[/]");
            progress.WriteException(ex);
            ctx.ExitCode = ExitUnexpected;
        }
    }

    // ---------------------------------------------------------------------------
    // Console helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Creates a progress console that writes to <see cref="Console.Error"/> so that
    /// structured output (JSON, SARIF, …) on stdout is never polluted.
    /// </summary>
    private static IAnsiConsole CreateProgressConsole(bool noColor) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(Console.Error),
        });

    private static void RenderHeader(IAnsiConsole console, string assemblyPath, string? contextName, string connection)
    {
        console.Write(new Rule("[bold blue]ef-schema-diff[/]").RuleStyle(Style.Parse("blue dim")));

        var grid = new Grid()
            .AddColumn(new GridColumn().Width(12).NoWrap())
            .AddColumn(new GridColumn());

        grid.AddRow("[dim]Assembly[/]", $"[bold]{Markup.Escape(Path.GetFileName(assemblyPath))}[/]");
        if (!string.IsNullOrWhiteSpace(contextName))
            grid.AddRow("[dim]Context[/]", $"[bold]{Markup.Escape(contextName)}[/]");
        grid.AddRow("[dim]Database[/]", $"[bold]{Markup.Escape(ExtractServerInfo(connection))}[/]");

        console.Write(grid);
        console.WriteLine();
    }

    /// <summary>Extracts server/database from the connection string without exposing credentials.</summary>
    private static string ExtractServerInfo(string connectionString)
    {
        string? server = null, database = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) continue;
            var key   = part[..idx].Trim().ToUpperInvariant();
            var value = part[(idx + 1)..].Trim();
            if (key is "SERVER" or "DATA SOURCE")      server   = value;
            else if (key is "DATABASE" or "INITIAL CATALOG") database = value;
        }
        return (server, database) switch
        {
            ({ } s, { } d) => $"{s} / {d}",
            ({ } s, null)  => s,
            (null, { } d)  => d,
            _              => "(connection string)",
        };
    }

    private static void RenderException(IAnsiConsole console, Exception ex)
    {
        if (ex.InnerException is not null)
            console.WriteException(ex, ExceptionFormats.ShortenEverything);
        else
            console.MarkupLine($"  [red]{Markup.Escape(ex.Message)}[/]");
    }

    private static string BuildSummaryMarkup(SchemaDiffResult result)
    {
        if (!result.HasDifferences) return "[green]✔ No differences[/]";

        var parts = new List<string>();
        if (result.ErrorCount   > 0) parts.Add($"[red]{result.ErrorCount} {Pluralize(result.ErrorCount, "error", "errors")}[/]");
        if (result.WarningCount > 0) parts.Add($"[yellow]{result.WarningCount} {Pluralize(result.WarningCount, "warning", "warnings")}[/]");
        if (result.InfoCount    > 0) parts.Add($"[blue]{result.InfoCount} info[/]");

        var icon = result.ErrorCount > 0 ? "[red]✘[/]" : "[yellow]⚠[/]";
        return $"{icon} {result.DifferenceCount} {Pluralize(result.DifferenceCount, "difference", "differences")}  {string.Join("  [dim]·[/]  ", parts)}";
    }

    private static string Pluralize(int count, string singular, string plural) =>
        count == 1 ? singular : plural;

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static bool IsDbException(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        return typeName.Contains("SqlException", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)
               || (ex.InnerException is not null && IsDbException(ex.InnerException));
    }
}
