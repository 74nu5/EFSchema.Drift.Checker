using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public interface IOutputFormatter
{
    string FormatName { get; }
    string Format(SchemaDiffResult result);
}
