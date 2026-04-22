namespace EfSchemaDiff.Core.Models;

public enum DiffType
{
    TableMissingInDatabase,
    TableMissingInModel,
    ColumnMissingInDatabase,
    ColumnMissingInModel,
    ColumnTypeMismatch,
    NullabilityMismatch,
    MaxLengthMismatch,
    PrecisionMismatch,
    ScaleMismatch,
    PrimaryKeyMismatch,
    ForeignKeyMismatch,
    IndexMissingInDatabase,
    IndexMissingInModel,
    UniqueConstraintMismatch,
    SchemaMismatch
}
