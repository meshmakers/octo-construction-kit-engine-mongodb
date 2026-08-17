using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

/// <summary>
///     Pure logic of the display-rule backfill sweep (AB#4812): change detection on import and the
///     per-entity partial-update planning (write only changed fields; empty string = clear sentinel).
/// </summary>
public class DisplayRuleSweepTests
{
    // ── Change detection ────────────────────────────────────────────────────────────────

    private static Dictionary<string, DeclaredDisplayRules> Rules(
        params (string TypeId, string? Name, string? Description)[] entries)
    {
        return entries.ToDictionary(e => e.TypeId, e => new DeclaredDisplayRules(e.Name, e.Description));
    }

    [Fact]
    public void ChangeDetector_NoChanges_Empty()
    {
        var rules = Rules(("M/A", "${Name}", null), ("M/B", null, null));

        Assert.Empty(DisplayRuleChangeDetector.GetChangedTypeIds(rules, rules));
    }

    [Fact]
    public void ChangeDetector_RuleAddedChangedRemoved_Detected()
    {
        var before = Rules(("M/A", null, null), ("M/B", "${Name}", null), ("M/C", "${Name}", "${Description}"));
        var after = Rules(("M/A", "${Name}", null), ("M/B", "${Name} (${Id})", null), ("M/C", "${Name}", null));

        var changed = DisplayRuleChangeDetector.GetChangedTypeIds(before, after);

        Assert.Equal(3, changed.Count);
        Assert.Contains("M/A", changed);
        Assert.Contains("M/B", changed);
        Assert.Contains("M/C", changed);
    }

    [Fact]
    public void ChangeDetector_NewTypeWithoutRule_NotDetected()
    {
        var before = Rules();
        var after = Rules(("M/A", null, null));

        Assert.Empty(DisplayRuleChangeDetector.GetChangedTypeIds(before, after));
    }

    [Fact]
    public void ChangeDetector_NewTypeWithRule_Detected()
    {
        var before = Rules();
        var after = Rules(("M/A", "${Name}", null));

        Assert.Equal(["M/A"], DisplayRuleChangeDetector.GetChangedTypeIds(before, after));
    }

    [Fact]
    public void ChangeDetector_RemovedType_NotDetected()
    {
        var before = Rules(("M/A", "${Name}", null));
        var after = Rules();

        Assert.Empty(DisplayRuleChangeDetector.GetChangedTypeIds(before, after));
    }

    [Fact]
    public void ChangeDetector_WhitespaceOnlyDifference_NotDetected()
    {
        var before = Rules(("M/A", "${Name}", null));
        var after = Rules(("M/A", " ${Name} ", ""));

        Assert.Empty(DisplayRuleChangeDetector.GetChangedTypeIds(before, after));
    }

    // ── Partial update planning ─────────────────────────────────────────────────────────

    private const string TenantId = "test";

    private static ICkCacheService CreateCacheService(string? displayNameRule,
        string? displayDescriptionRule = null)
    {
        var ckTypeGraph = new CkTypeGraph(new CkId<CkTypeId>("Test/Space"), new CkCompiledTypeDto
        {
            TypeId = "Space",
            DisplayNameRule = displayNameRule,
            DisplayDescriptionRule = displayDescriptionRule
        });

        var ckCacheService = A.Fake<ICkCacheService>();
        A.CallTo(() => ckCacheService.GetRtCkType(TenantId, A<RtCkId<CkTypeId>>._)).Returns(ckTypeGraph);
        return ckCacheService;
    }

    private static RtEntity CreateEntity(Dictionary<string, object?> attributes,
        string? storedDisplayName = null, string? storedDisplayDescription = null)
    {
        return new RtEntity(new RtCkId<CkTypeId>("Test/Space"), OctoObjectId.GenerateNewId(), attributes)
        {
            RtDisplayName = storedDisplayName,
            RtDisplayDescription = storedDisplayDescription
        };
    }

    [Fact]
    public void CreatePartialUpdate_ValueMatches_NoWrite()
    {
        var cache = CreateCacheService("${Name}");
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich" }, "Wohnbereich");

        Assert.Null(DisplayRuleSweeper.CreatePartialUpdate(cache, TenantId, entity));
    }

    [Fact]
    public void CreatePartialUpdate_ValueDiffers_WritesNewValue()
    {
        var cache = CreateCacheService("${Name}");
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich" }, storedDisplayName: null);

        var update = DisplayRuleSweeper.CreatePartialUpdate(cache, TenantId, entity);

        Assert.NotNull(update);
        Assert.Equal(entity.RtId, update!.RtId);
        Assert.Equal("Wohnbereich", update.RtDisplayName);
        Assert.Null(update.RtDisplayDescription);
    }

    [Fact]
    public void CreatePartialUpdate_RuleRemovedOrEmpty_WritesClearSentinel()
    {
        var cache = CreateCacheService(null);
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich" }, "Alter Anzeigename", "Alte Beschreibung");

        var update = DisplayRuleSweeper.CreatePartialUpdate(cache, TenantId, entity);

        Assert.NotNull(update);
        Assert.Equal(string.Empty, update!.RtDisplayName);
        Assert.Equal(string.Empty, update.RtDisplayDescription);
    }

    [Fact]
    public void CreatePartialUpdate_OnlyDescriptionChanges_NameLeftUntouched()
    {
        var cache = CreateCacheService("${Name}", "${Description}");
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich", ["Description"] = "Neu" },
            "Wohnbereich", "Alt");

        var update = DisplayRuleSweeper.CreatePartialUpdate(cache, TenantId, entity);

        Assert.NotNull(update);
        Assert.Null(update!.RtDisplayName);
        Assert.Equal("Neu", update.RtDisplayDescription);
    }

    [Fact]
    public void CreatePartialUpdate_BothNullAndNoRules_NoWrite()
    {
        var cache = CreateCacheService(null);
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich" });

        Assert.Null(DisplayRuleSweeper.CreatePartialUpdate(cache, TenantId, entity));
    }
}
