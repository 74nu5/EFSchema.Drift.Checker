using EfSchemaDiff.Infrastructure.Providers.SqlServer;

namespace EfSchemaDiff.Tests.Normalizer;

public sealed class SqlServerStoreTypeNormalizerTests
{
    private readonly SqlServerStoreTypeNormalizer _sut = new();

    [Theory]
    [InlineData("integer",            "int")]
    [InlineData("INTEGER",            "int")]
    [InlineData("boolean",            "bit")]
    [InlineData("bool",               "bit")]
    [InlineData("timestamp",          "rowversion")]
    [InlineData("character varying",  "varchar")]
    [InlineData("national char varying", "nvarchar")]
    [InlineData("double precision",   "float")]
    [InlineData("dec",                "decimal")]
    public void Normalize_AppliesAliasMapping(string input, string expected)
        => _sut.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("numeric(18,2)",  "decimal(18,2)")]
    [InlineData("NUMERIC(10,4)",  "decimal(10,4)")]
    [InlineData("dec(10,2)",      "decimal(10,2)")]
    public void Normalize_AppliesAliasMapping_WithPrecisionScale(string input, string expected)
        => _sut.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("nvarchar(max)",   "nvarchar")]
    [InlineData("NVARCHAR(MAX)",   "nvarchar")]
    [InlineData("varchar(max)",    "varchar")]
    [InlineData("varbinary(max)",  "varbinary")]
    [InlineData("nvarchar( MAX )", "nvarchar")]
    public void Normalize_StripsMaxSuffix(string input, string expected)
        => _sut.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("nvarchar(100)",   "nvarchar(100)")]
    [InlineData("decimal(18,2)",   "decimal(18,2)")]
    [InlineData("char(10)",        "char(10)")]
    [InlineData("varchar(255)",    "varchar(255)")]
    public void Normalize_PreservesSpecificSizes(string input, string expected)
        => _sut.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsEmpty_ForNullOrWhitespace(string? input)
        => _sut.Normalize(input).Should().BeEmpty();

    [Fact]
    public void Normalize_IsLowerCase()
        => _sut.Normalize("BIGINT").Should().Be("bigint");

    [Fact]
    public void Normalize_PassThrough_UnknownType()
        => _sut.Normalize("uniqueidentifier").Should().Be("uniqueidentifier");
}
