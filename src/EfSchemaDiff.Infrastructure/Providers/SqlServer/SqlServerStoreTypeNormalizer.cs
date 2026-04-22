using System.Text.RegularExpressions;
using EfSchemaDiff.Core.Interfaces;

namespace EfSchemaDiff.Infrastructure.Providers.SqlServer;

public sealed partial class SqlServerStoreTypeNormalizer : IStoreTypeNormalizer
{
    // Matches a trailing (max) suffix, case-insensitive.
    [GeneratedRegex(@"\(\s*max\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MaxSuffixRegex();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["integer"]            = "int",
        ["numeric"]            = "decimal",
        ["bool"]               = "bit",
        ["boolean"]            = "bit",
        ["timestamp"]          = "rowversion",
        ["character varying"]  = "varchar",
        ["national char varying"] = "nvarchar",
        ["double precision"]   = "float",
        ["dec"]                = "decimal",
    };

    public string Normalize(string? storeType)
    {
        if (string.IsNullOrWhiteSpace(storeType))
            return string.Empty;

        var normalized = storeType.Trim().ToLowerInvariant();

        // Apply alias mappings.  For aliases that can carry precision/scale (numeric/dec),
        // we only replace the type name part and keep any parenthesised suffix.
        foreach (var (alias, canonical) in Aliases)
        {
            var aliasLower = alias.ToLowerInvariant();
            if (normalized == aliasLower)
            {
                normalized = canonical;
                break;
            }

            // e.g. "numeric(18,2)" → "decimal(18,2)"
            if (normalized.StartsWith(aliasLower + "(", StringComparison.Ordinal))
            {
                normalized = canonical + normalized[aliasLower.Length..];
                break;
            }
        }

        // Strip trailing (max) — `nvarchar(max)` → `nvarchar`.
        normalized = MaxSuffixRegex().Replace(normalized, string.Empty).TrimEnd();

        return normalized;
    }
}
