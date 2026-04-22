using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public interface IEfModelExtractor
{
    /// <summary>Extracts the schema from an EF Core IModel. Prefer this overload for unit testing.</summary>
    DatabaseSchema Extract(IModel model);

    /// <summary>Convenience overload that extracts directly from a DbContext instance.</summary>
    DatabaseSchema Extract(DbContext dbContext);
}
