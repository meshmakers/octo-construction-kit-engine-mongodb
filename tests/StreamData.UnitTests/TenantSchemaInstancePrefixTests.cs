using Meshmakers.Octo.Runtime.Engine.CrateDb;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     Pins the AB#4946 CrateDB schema instance prefix (Epic AB#4944). The load-bearing contract
///     is backwards compatibility: with NO prefix configured, every schema name must be
///     byte-identical to the pre-AB#4946 naming — existing instances' schemas must never move.
/// </summary>
[Collection("TenantSchemaInstancePrefix")]
[CollectionDefinition("TenantSchemaInstancePrefix", DisableParallelization = true)]
public sealed class TenantSchemaInstancePrefixTests : IDisposable
{
    public TenantSchemaInstancePrefixTests()
    {
        TenantSchema.ResetInstancePrefixForTests();
    }

    public void Dispose()
    {
        // The prefix is process-wide state — never leak it into other test classes.
        TenantSchema.ResetInstancePrefixForTests();
    }

    [Fact]
    public void SchemaName_WithoutPrefix_IsByteIdenticalToLegacyNaming()
    {
        // Regression pin: these exact values are what the pre-AB#4946 implementation produced.
        // Uses the pure naming core so the pin holds regardless of the process-wide state.
        Assert.Equal("acmecorp", TenantSchema.SchemaName("acme-corp", string.Empty));
        Assert.Equal("fdaseen", TenantSchema.SchemaName("fda-seen", string.Empty));
        Assert.Equal("meshtest", TenantSchema.SchemaName("MeshTest", string.Empty));

        var longTenant = new string('a', 100);
        var longName = TenantSchema.SchemaName(longTenant, string.Empty);
        Assert.Equal(TenantSchema.MaxSchemaLength, longName.Length);
        Assert.StartsWith(new string('a', 46), longName);
        Assert.Contains("_", longName);

        Assert.Equal("\"acmecorp\".\"streamData\"", TenantSchema.QualifiedLegacyTable("acme-corp"));
        Assert.Equal("\"acmecorp\".\"archive_65d5c447b420da3fb12381bc\"",
            TenantSchema.QualifiedArchiveTable("acme-corp", "65d5c447b420da3fb12381bc"));
    }

    [Fact]
    public void SchemaName_WithPrefix_PrependsIt()
    {
        TenantSchema.SetInstancePrefix("dev");

        Assert.Equal("dev_acmecorp", TenantSchema.SchemaName("acme-corp"));
        Assert.Equal("\"dev_acmecorp\".\"archive_65d5c447b420da3fb12381bc\"",
            TenantSchema.QualifiedArchiveTable("acme-corp", "65d5c447b420da3fb12381bc"));
    }

    [Fact]
    public void SetInstancePrefix_CleansToLowercaseAlphanumeric()
    {
        TenantSchema.SetInstancePrefix("Dev-0.2");

        Assert.Equal("dev02", TenantSchema.InstancePrefix);
        Assert.Equal("dev02_meshtest", TenantSchema.SchemaName("meshtest"));
    }

    [Fact]
    public void SchemaName_WithPrefix_KeepsHashFallbackInsideBudget()
    {
        var longTenant = new string('a', 100);
        var name = TenantSchema.SchemaName(longTenant, "dev");

        Assert.Equal(TenantSchema.MaxSchemaLength, name.Length);
        Assert.StartsWith("dev_", name);
        // Distinct long tenants must stay distinct through the hash suffix.
        var other = TenantSchema.SchemaName(new string('b', 100), "dev");
        Assert.NotEqual(name, other);
    }

    [Fact]
    public void SetInstancePrefix_IsIdempotent_ButConflictingValueThrows()
    {
        TenantSchema.SetInstancePrefix("dev");
        TenantSchema.SetInstancePrefix("dev");
        TenantSchema.SetInstancePrefix("DEV");
        // A late consumer without the setting must not clear an already-configured prefix.
        TenantSchema.SetInstancePrefix(null);
        TenantSchema.SetInstancePrefix(string.Empty);
        Assert.Equal("dev", TenantSchema.InstancePrefix);

        Assert.Throws<InvalidOperationException>(() => TenantSchema.SetInstancePrefix("other"));
    }

    [Fact]
    public void SetInstancePrefix_NonAlphanumericOnly_Throws()
    {
        Assert.Throws<ArgumentException>(() => TenantSchema.SetInstancePrefix("---"));
    }

    [Fact]
    public void SetInstancePrefix_ConfiguredAfterEmptyInitialization_Wins()
    {
        TenantSchema.SetInstancePrefix(null);
        TenantSchema.SetInstancePrefix("dev");

        Assert.Equal("dev", TenantSchema.InstancePrefix);
    }
}
