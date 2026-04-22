using EfSchemaDiff.Core.Models;

namespace EfSchemaDiff.Core.Interfaces;

public interface ISchemaComparer
{
    SchemaDiffResult Compare(
        DatabaseSchema efSchema,
        DatabaseSchema databaseSchema,
        SchemaCompareOptions options);
}
