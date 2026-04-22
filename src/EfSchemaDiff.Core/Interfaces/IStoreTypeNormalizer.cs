using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public interface IStoreTypeNormalizer
{
    /// <summary>Normalizes a SQL store type string to a canonical form for comparison.</summary>
    string Normalize(string? storeType);
}
