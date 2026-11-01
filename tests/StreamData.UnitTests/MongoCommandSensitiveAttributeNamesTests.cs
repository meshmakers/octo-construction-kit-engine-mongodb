using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// AB#8009: the set of attribute names masked in rendered MongoDB commands.
public sealed class MongoCommandSensitiveAttributeNamesTests
{
    [Fact]
    public void WithoutACkCache_OnlyTheKnownCredentialNamesApply()
    {
        var sut = new MongoCommandSensitiveAttributeNames((ICkCacheService?)null);

        var names = sut.GetFor("tenant_a");

        Assert.Equal(MongoCommandSensitiveAttributeNames.KnownCredentialNames.Order(), names.Order());
        Assert.Contains("PasswordHash", names); // case-insensitive
        Assert.Contains("clientSecrets", names);
    }

    [Fact]
    public void TenantNames_AreAdded_AndTheSetOnlyGrows()
    {
        var sut = new MongoCommandSensitiveAttributeNames(db => db switch
        {
            "tenant_a" => ["Pin"],
            "tenant_b" => ["Alias"],
            _ => []
        }, new ManualTimeProvider());

        Assert.Contains("pin", sut.GetFor("tenant_a"));
        var afterB = sut.GetFor("tenant_b");

        Assert.Contains("pin", afterB);
        Assert.Contains("alias", afterB);
        Assert.Contains("passwordHash", afterB);
        Assert.Contains("pin", sut.GetFor("unknown"));
        Assert.Contains("alias", sut.GetFor(null));
    }

    [Fact]
    public void TenantNames_AreReadAtMostOncePerRefreshInterval()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var sut = new MongoCommandSensitiveAttributeNames(_ =>
        {
            calls++;
            return calls == 1 ? ["Pin"] : ["Pin", "Token"];
        }, time);

        sut.GetFor("tenant_a");
        sut.GetFor("tenant_a");
        Assert.Equal(1, calls);
        Assert.DoesNotContain("token", sut.GetFor("tenant_a"));

        time.Advance(MongoCommandSensitiveAttributeNames.RefreshInterval);

        Assert.Contains("token", sut.GetFor("tenant_a"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ACacheThatIsNotLoaded_IsAskedAgainWithTheNextCommand()
    {
        var calls = 0;
        var sut = new MongoCommandSensitiveAttributeNames(_ => ++calls < 3 ? null : ["Pin"], new ManualTimeProvider());

        Assert.DoesNotContain("pin", sut.GetFor("tenant_a"));
        Assert.DoesNotContain("pin", sut.GetFor("tenant_a"));
        Assert.Contains("pin", sut.GetFor("tenant_a"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void AFailingCache_NeverThrows_KeepsTheKnownNames_AndIsNotRetriedWithinTheInterval()
    {
        var calls = 0;
        var sut = new MongoCommandSensitiveAttributeNames(_ =>
        {
            calls++;
            throw new InvalidOperationException("cache broken");
        }, new ManualTimeProvider());

        Assert.Contains("passwordHash", sut.GetFor("tenant_a"));
        Assert.Contains("passwordHash", sut.GetFor("tenant_a"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CollectFromCkCache_TenantNotLoaded_ReturnsNull()
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded("tenant_a")).Returns(false);

        Assert.Null(MongoCommandSensitiveAttributeNames.CollectFromCkCache(cache, "tenant_a"));
        A.CallTo(() => cache.GetCkTypes(A<string>._)).MustNotHaveHappened();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
