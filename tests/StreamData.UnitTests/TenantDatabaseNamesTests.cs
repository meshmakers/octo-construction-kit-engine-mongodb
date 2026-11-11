using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

/// <summary>
///     Naming rules of the tenant-database prefix (AB#8797) and the validation of the setting.
/// </summary>
public class TenantDatabaseNamesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutPrefix_TheDefaultNameIsTheNormalizedTenantId(string? prefix)
    {
        Assert.Equal("meshtest", TenantDatabaseNames.DeriveDefault(prefix, " MeshTest "));
        Assert.True(TenantDatabaseNames.CarriesPrefix(prefix, "anything"));
    }

    [Fact]
    public void WithPrefix_TheDefaultNameIsPrefixPlusTenantId_Normalized()
    {
        Assert.Equal("s2_meshtest", TenantDatabaseNames.DeriveDefault(" S2_ ", "MeshTest"));
    }

    [Theory]
    [InlineData("s2_meshtest", true)]
    [InlineData("S2_MeshTest", true)]
    [InlineData(" s2_x ", true)]
    [InlineData("meshtest", false)]
    [InlineData("s3_meshtest", false)]
    [InlineData("xs2_meshtest", false)]
    public void CarriesPrefix_ComparesTheNormalizedName(string databaseName, bool expected)
    {
        Assert.Equal(expected, TenantDatabaseNames.CarriesPrefix("s2_", databaseName));
    }

    [Theory]
    [InlineData("s2_")]
    [InlineData("dev-")]
    [InlineData("A1")]
    public void Configuration_AcceptsAndNormalizesAValidPrefix(string prefix)
    {
        var configuration = new OctoSystemConfiguration { TenantDatabasePrefix = prefix };

        Assert.Equal(prefix.ToLowerInvariant(), configuration.TenantDatabasePrefix);
    }

    [Fact]
    public void Configuration_DefaultsToNoPrefix()
    {
        Assert.Equal(string.Empty, new OctoSystemConfiguration().TenantDatabasePrefix);
        Assert.Equal(string.Empty, new OctoSystemConfiguration { TenantDatabasePrefix = "  " }.TenantDatabasePrefix);
    }

    [Theory]
    [InlineData("dev.")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a$")]
    [InlineData("ä")]
    [InlineData("1234567890123456789012345678901")]
    public void Configuration_RefusesAnInvalidPrefix_NamingTheOption(string prefix)
    {
        var exception = Assert.Throws<ConfigurationErrorException>(() =>
            new OctoSystemConfiguration { TenantDatabasePrefix = prefix });

        Assert.Contains(nameof(OctoSystemConfiguration.TenantDatabasePrefix), exception.Message);
    }

    [Fact]
    public void Exemption_CoversOnlyTheNameAndOnlyInsideTheScope()
    {
        Assert.False(TenantDatabasePrefixExemption.IsExempt("legacy"));

        using (TenantDatabasePrefixExemption.Begin(" Legacy "))
        {
            Assert.True(TenantDatabasePrefixExemption.IsExempt("legacy"));
            Assert.False(TenantDatabasePrefixExemption.IsExempt("other"));
        }

        Assert.False(TenantDatabasePrefixExemption.IsExempt("legacy"));
    }

    [Fact]
    public async Task Exemption_DoesNotLeakIntoAnotherAsyncFlow()
    {
        using var _ = TenantDatabasePrefixExemption.Begin("legacy");

        var seenElsewhere = await Task.Run(() =>
        {
            using (ExecutionContextSuppressed())
            {
                return Task.Run(() => TenantDatabasePrefixExemption.IsExempt("legacy"));
            }
        });

        Assert.False(seenElsewhere);
        Assert.True(TenantDatabasePrefixExemption.IsExempt("legacy"));
    }

    private static IDisposable ExecutionContextSuppressed() => new SuppressFlow(ExecutionContext.SuppressFlow());

    private sealed class SuppressFlow(AsyncFlowControl control) : IDisposable
    {
        public void Dispose() => control.Undo();
    }
}
