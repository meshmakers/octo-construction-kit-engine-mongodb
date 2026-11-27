using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests.RestoreSystemModel;

/// <summary>
///     The decision table of the restore check of service-managed models, one test per branch. The database and catalog
///     side is covered by <c>RestoreSystemModelCheckTests</c> in the integration tests.
/// </summary>
public class RestoreSystemModelDeciderTests
{
    private static readonly CkVersion IdentityFloor = new(3, 6, 0);

    private static RestoreSystemModelFacts Genuine(string dump, string? platform, bool platformIdentical = true,
        CkVersion? floor = null, int highestMajor = 3) =>
        new(new CkModelId(dump), PublishedWithSameId: true, ContentEqualsCatalog: true, highestMajor,
            platform == null ? null : new CkModelId(platform), platformIdentical, floor);

    [Fact]
    public void SameVersionAsThePlatform_IsKept()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Identity-3.8.0", "System.Identity-3.8.0",
            floor: IdentityFloor));

        Assert.Equal(RestoreSystemModelAction.Keep, decision.Action);
        Assert.Equal(new CkModelId("System.Identity-3.8.0"), decision.Target);
    }

    [Fact]
    public void GenuineOlderThanThePlatform_IsRaisedToExactlyThePlatformsVersion()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Identity-3.5.0", "System.Identity-3.8.0",
            floor: IdentityFloor));

        Assert.Equal(RestoreSystemModelAction.Raise, decision.Action);
        Assert.Equal(new CkModelId("System.Identity-3.8.0"), decision.Target);
    }

    [Fact]
    public void GenuineNewerThanThePlatform_SameMajor_IsKept_NeverDowngraded()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Identity-3.9.0", "System.Identity-3.8.0",
            floor: IdentityFloor));

        Assert.Equal(RestoreSystemModelAction.Keep, decision.Action);
        Assert.Equal(new CkModelId("System.Identity-3.9.0"), decision.Target);
    }

    [Fact]
    public void GenuineModelTheSystemTenantDoesNotHold_IsKept()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Reporting-1.2.0", null));

        Assert.Equal(RestoreSystemModelAction.Keep, decision.Action);
    }

    [Fact]
    public void GenuineOfAnotherMajorThanThePlatform_IsKept_WhenItReachesTheFloor()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System-2.9.0", "System-3.0.0"));

        Assert.Equal(RestoreSystemModelAction.Keep, decision.Action);
    }

    [Fact]
    public void GenuineBelowTheFloor_TheSystemTenantDoesNotHoldIt_IsRefused()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Identity-3.5.0", null, floor: IdentityFloor));

        Assert.Equal(RestoreSystemModelAction.Refuse, decision.Action);
        Assert.Equal(RestoreSystemModelRefusalReason.BelowFloor, decision.Reason);
    }

    [Fact]
    public void SystemTenantBelowTheFloor_TheRaiseWouldStayBelowIt_IsRefused()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Identity-3.4.0", "System.Identity-3.5.0",
            floor: IdentityFloor));

        Assert.Equal(RestoreSystemModelAction.Refuse, decision.Action);
        Assert.Equal(RestoreSystemModelRefusalReason.BelowFloor, decision.Reason);
        Assert.Contains("System.Identity-3.5.0", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void GenuineOlder_ThePlatformsVersionIsNotPublishedIdentically_IsRefused_NeverRaisedToIt()
    {
        var decision = RestoreSystemModelDecider.Decide(Genuine("System.Identity-3.7.0", "System.Identity-3.8.0",
            platformIdentical: false, floor: IdentityFloor));

        Assert.Equal(RestoreSystemModelAction.Refuse, decision.Action);
        Assert.Equal(RestoreSystemModelRefusalReason.TargetNotPublished, decision.Reason);
    }

    [Theory]
    [InlineData("System.Identity-3.8.0")]
    [InlineData("System.Identity-3.5.0")]
    public void ManipulatedContent_IsRefused_AlsoWhenTheVersionIsOlderThanThePlatform(string dump)
    {
        var facts = Genuine(dump, "System.Identity-3.8.0", floor: IdentityFloor) with { ContentEqualsCatalog = false };

        var decision = RestoreSystemModelDecider.Decide(facts);

        Assert.Equal(RestoreSystemModelAction.Refuse, decision.Action);
        Assert.Equal(RestoreSystemModelRefusalReason.ContentDiffers, decision.Reason);
    }

    [Fact]
    public void UnpublishedVersionOfAPublishedMajor_IsRefused()
    {
        var facts = Genuine("System.Identity-3.99.0", "System.Identity-3.8.0") with { PublishedWithSameId = false };

        var decision = RestoreSystemModelDecider.Decide(facts);

        Assert.Equal(RestoreSystemModelRefusalReason.UnknownVersion, decision.Reason);
    }

    [Fact]
    public void HigherMajorThanPublished_IsRefused()
    {
        var facts = Genuine("System.Identity-4.0.0", "System.Identity-3.8.0") with { PublishedWithSameId = false };

        var decision = RestoreSystemModelDecider.Decide(facts);

        Assert.Equal(RestoreSystemModelRefusalReason.HigherMajor, decision.Reason);
    }

    [Fact]
    public void ModelNameTheCatalogDoesNotKnow_IsRefused()
    {
        var facts = new RestoreSystemModelFacts(new CkModelId("System.Foo-1.0.0"), false, false, null, null, false,
            null);

        var decision = RestoreSystemModelDecider.Decide(facts);

        Assert.Equal(RestoreSystemModelRefusalReason.UnknownModel, decision.Reason);
    }

    [Fact]
    public void RefusalDetail_NamesVersionsOnly()
    {
        var decision = RestoreSystemModelDecider.Decide(
            Genuine("System.Identity-3.8.0", null) with { ContentEqualsCatalog = false });

        Assert.DoesNotContain("writeAccess", decision.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(decision.Target);
    }

    [Theory]
    [InlineData("System.Identity", "3.6.0")]
    [InlineData("system.identity", "3.6.0")]
    [InlineData("System.Identity ", "3.6.0")]
    [InlineData("System", null)]
    [InlineData("System.Bot", null)]
    public void Floors_KnowSystemIdentityOnly(string name, string? floor)
    {
        Assert.Equal(floor, ServiceManagedCkModelFloors.FloorOf(name)?.ToString());
    }
}
