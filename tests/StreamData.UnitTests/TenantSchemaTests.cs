using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class TenantSchemaTests
{
    [Theory]
    [InlineData("acmeCorp", "acmecorp")]
    [InlineData("ACME", "acme")]
    [InlineData("acme-corp", "acmecorp")]
    [InlineData("acme.corp.v2", "acmecorpv2")]
    [InlineData("acme/corp:1.0", "acmecorp10")]
    [InlineData("Acme_Corp 123", "acmecorp123")]
    public void SchemaName_StripsNonAlphanumericAndLowercases(string tenantId, string expected)
    {
        Assert.Equal(expected, TenantSchema.SchemaName(tenantId));
    }

    [Fact]
    public void SchemaName_ShortInput_PassesThroughUntouchedExceptCase()
    {
        Assert.Equal("test", TenantSchema.SchemaName("test"));
    }

    [Fact]
    public void SchemaName_AtBoundary_NoTruncation()
    {
        // 63 alphanumerics → exactly fits, no hash suffix.
        var input = new string('a', 63);
        var result = TenantSchema.SchemaName(input);
        Assert.Equal(63, result.Length);
        Assert.DoesNotContain("_", result);
    }

    [Fact]
    public void SchemaName_OverflowsLimit_AppendsDeterministicHashSuffix()
    {
        var input = new string('a', 80);
        var result = TenantSchema.SchemaName(input);

        Assert.Equal(63, result.Length);
        Assert.Contains("_", result);
        // Hash suffix is the last 16 hex chars.
        var suffix = result[^16..];
        Assert.Matches("^[0-9a-f]{16}$", suffix);
    }

    [Fact]
    public void SchemaName_Deterministic_SameInputProducesSameOutput()
    {
        var input = new string('x', 100);
        Assert.Equal(TenantSchema.SchemaName(input), TenantSchema.SchemaName(input));
    }

    [Fact]
    public void SchemaName_DifferentLongInputs_HaveDifferentHashSuffixes()
    {
        var a = TenantSchema.SchemaName("tenant" + new string('a', 100));
        var b = TenantSchema.SchemaName("tenant" + new string('b', 100));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SchemaName_EmptyTenantId_Throws()
    {
        Assert.Throws<ArgumentException>(() => TenantSchema.SchemaName(""));
    }

    [Fact]
    public void SchemaName_OnlySymbols_Throws()
    {
        Assert.Throws<ArgumentException>(() => TenantSchema.SchemaName("---"));
    }

    [Fact]
    public void QualifiedLegacyTable_FormatsAsQuotedSchemaDotQuotedTable()
    {
        Assert.Equal("\"acmecorp\".\"streamData\"", TenantSchema.QualifiedLegacyTable("acmeCorp"));
    }

    [Fact]
    public void QuotedSchema_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"acmecorp\"", TenantSchema.QuotedSchema("acmeCorp"));
    }
}
