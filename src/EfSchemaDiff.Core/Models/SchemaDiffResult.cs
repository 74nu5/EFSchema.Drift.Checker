namespace EfSchemaDiff.Core.Models;

public sealed class SchemaDiffResult
{
    public bool HasDifferences => Differences.Count > 0;
    public int DifferenceCount => Differences.Count;

    public IReadOnlyList<SchemaDifference> Differences { get; init; } = [];

    public int ErrorCount => Differences.Count(d => d.Severity == DiffSeverity.Error);
    public int WarningCount => Differences.Count(d => d.Severity == DiffSeverity.Warning);
    public int InfoCount => Differences.Count(d => d.Severity == DiffSeverity.Info);
}
