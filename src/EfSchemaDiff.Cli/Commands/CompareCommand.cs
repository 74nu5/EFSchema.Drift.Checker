using System.CommandLine;
using System.CommandLine.Invocation;
using EfSchemaDiff.Cli.Formatters;
using EfSchemaDiff.Core.Interfaces;
using EfSchemaDiff.Core.Models;
using EfSchemaDiff.Infrastructure.Comparison;
using EfSchemaDiff.Infrastructure.EfCore;
using EfSchemaDiff.Infrastructure.Loading;
using EfSchemaDiff.Infrastructure.Providers.SqlServer;

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

        var assemblyPath    = pr.GetValueForOption(AssemblyOption)!;
        var contextName     = pr.GetValueForOption(ContextOption);
        var startupAssembly = pr.GetValueForOption(StartupAssemblyOption);
        var schema          = pr.GetValueForOption(SchemaOption);
        var ignoreTables    = pr.GetValueForOption(IgnoreTablesOption) ?? [];
        var ignoreColumns   = pr.GetValueForOption(IgnoreColumnsOption) ?? [];
        var excludeNav      = pr.GetValueForOption(ExcludeNavTablesOption);
        var outputFormat    = pr.GetValueForOption(OutputOption) ?? "table";
        var outputFile      = pr.GetValueForOption(OutputFileOption);
        var noColor         = pr.GetValueForOption(NoColorOption);
        var minSeverityRaw  = pr.GetValueForOption(MinSeverityOption) ?? "Warning";

        // Exit-code constants
        const int ExitOk              = 0;
        const int ExitDifferences     = 1;
        const int ExitBadConfig       = 2;
        const int ExitDbError         = 3;
        const int ExitContextError    = 4;
        const int ExitUnexpected      = 5;

        // -- Validate assembly path --
        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine($"[error] Assembly not found: {assemblyPath}");
            ctx.ExitCode = ExitBadConfig;
            return;
        }

        // -- Validate / parse minimum severity --
        if (!Enum.TryParse<DiffSeverity>(minSeverityRaw, ignoreCase: true, out var minSeverity))
        {
            Console.Error.WriteLine($"[error] Unknown severity '{minSeverityRaw}'. Valid values: Error, Warning, Info.");
            ctx.ExitCode = ExitBadConfig;
            return;
        }

        // -- Resolve connection string --
        var connection = pr.GetValueForOption(ConnectionOption)
                         ?? Environment.GetEnvironmentVariable("EFSD_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("[error] Connection string is required. Use --connection or set EFSD_CONNECTION.");
            ctx.ExitCode = ExitBadConfig;
            return;
        }

        var cancellationToken = ctx.GetCancellationToken();

        try
        {
            // -- Load EF Core model --
            SchemaDiffResult diffResult;
            try
            {
                var efSchema = await new DbContextLoader(new EfModelExtractor())
                    .LoadAndExtractAsync(
                        assemblyPath,
                        startupAssembly,
                        contextName ?? string.Empty,
                        connection,
                        cancellationToken);

                // -- Read database schema --
                var dbSchema = await new SqlServerSchemaReader()
                    .ReadAsync(connection, cancellationToken);

                // -- Compare --
                var options = new SchemaCompareOptions
                {
                    Schema = schema,
                    IgnoreTables = ignoreTables,
                    IgnoreColumns = ignoreColumns,
                    ExcludeNavigationTables = excludeNav,
                };

                diffResult = new SchemaComparer(new SqlServerStoreTypeNormalizer())
                    .Compare(efSchema, dbSchema, options);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsDbException(ex))
            {
                Console.Error.WriteLine($"[error] Database connection failed: {ex.Message}");
                ctx.ExitCode = ExitDbError;
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[error] Could not load DbContext: {ex.Message}");
                ctx.ExitCode = ExitContextError;
                return;
            }

            // -- Filter by min severity --
            var filtered = new SchemaDiffResult
            {
                Differences = diffResult.Differences
                    .Where(d => d.Severity <= minSeverity)
                    .ToList()
            };

            // -- Format output --
            IOutputFormatter formatter = outputFormat.ToLowerInvariant() switch
            {
                "json"     => new JsonOutputFormatter(),
                "markdown" => new MarkdownOutputFormatter(),
                "sarif"    => new SarifOutputFormatter(),
                _          => new ConsoleOutputFormatter(noColor)
            };

            var output = formatter.Format(filtered);

            if (!string.IsNullOrEmpty(outputFile))
            {
                await File.WriteAllTextAsync(outputFile, output, cancellationToken);
                // Still print a summary to console when writing to file
                if (outputFormat.ToLowerInvariant() != "table")
                    Console.WriteLine(BuildSummaryLine(filtered));
            }
            else if (!string.IsNullOrEmpty(output))
            {
                Console.Write(output);
            }

            ctx.ExitCode = filtered.HasDifferences ? ExitDifferences : ExitOk;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[cancelled]");
            ctx.ExitCode = ExitUnexpected;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] Unexpected error: {ex}");
            ctx.ExitCode = ExitUnexpected;
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string BuildSummaryLine(SchemaDiffResult result)
    {
        if (!result.HasDifferences)
            return "✅ No schema differences found.";

        return $"⚠️  {result.DifferenceCount} difference(s): " +
               $"{result.ErrorCount} error(s), {result.WarningCount} warning(s), {result.InfoCount} info(s).";
    }

    private static bool IsDbException(Exception ex)
    {
        // Microsoft.Data.SqlClient exceptions and related timeout/connection exceptions
        var typeName = ex.GetType().FullName ?? string.Empty;
        return typeName.Contains("SqlException", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)
               || (ex.InnerException is not null && IsDbException(ex.InnerException));
    }
}
