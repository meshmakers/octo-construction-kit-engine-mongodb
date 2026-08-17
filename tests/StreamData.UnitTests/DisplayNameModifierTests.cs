using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

/// <summary>
///     DisplayNameModifier evaluates the CK type's effective display rules against entity
///     attributes before persistence (AB#4810).
/// </summary>
public class DisplayNameModifierTests
{
    private const string TenantId = "test";

    private static (DisplayNameModifier modifier, IOctoSession session, IRepositoryDataSource dataSource)
        CreateModifier(string? displayNameRule, string? displayDescriptionRule = null)
    {
        var ckTypeGraph = new CkTypeGraph(new CkId<CkTypeId>("Test/Space"), new CkCompiledTypeDto
        {
            TypeId = "Space",
            DisplayNameRule = displayNameRule,
            DisplayDescriptionRule = displayDescriptionRule
        });

        var ckCacheService = A.Fake<ICkCacheService>();
        A.CallTo(() => ckCacheService.GetRtCkType(TenantId, A<RtCkId<CkTypeId>>._)).Returns(ckTypeGraph);

        var dataSource = A.Fake<IRepositoryDataSource>();
        A.CallTo(() => dataSource.TenantId).Returns(TenantId);

        return (new DisplayNameModifier(ckCacheService), A.Fake<IOctoSession>(), dataSource);
    }

    private static RtEntity CreateEntity(Dictionary<string, object?> attributes)
    {
        return new RtEntity(new RtCkId<CkTypeId>("Test/Space"), OctoObjectId.GenerateNewId(), attributes);
    }

    [Fact]
    public async Task Run_ConcatenationRule_SetsDisplayName()
    {
        var (modifier, session, dataSource) = CreateModifier("${RoomNumber} - ${Name}");
        var entity = CreateEntity(new() { ["RoomNumber"] = "EG01", ["Name"] = "Wohnbereich" });

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Equal("EG01 - Wohnbereich", entity.RtDisplayName);
        Assert.Null(entity.RtDisplayDescription);
    }

    [Fact]
    public async Task Run_CoalesceRule_FallsBackToSecondPath()
    {
        var (modifier, session, dataSource) = CreateModifier("${Name ?? GlobalId}");
        var entity = CreateEntity(new() { ["Name"] = null, ["GlobalId"] = "space-eg-01" });

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Equal("space-eg-01", entity.RtDisplayName);
    }

    [Fact]
    public async Task Run_RecordPath_ResolvesNestedValue()
    {
        var (modifier, session, dataSource) = CreateModifier("${Thermal.SpaceTemperature} C");
        var record = new RtRecord(new RtCkId<CkRecordId>("Test/Requirements"),
            new Dictionary<string, object?> { ["SpaceTemperature"] = 21.5 });
        var entity = CreateEntity(new() { ["Thermal"] = record });

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Equal("21.5 C", entity.RtDisplayName);
    }

    [Fact]
    public async Task Run_AllReferencedAttributesEmpty_LeavesNull()
    {
        var (modifier, session, dataSource) = CreateModifier("${RoomNumber} - ${Name}");
        var entity = CreateEntity(new() { ["RoomNumber"] = null, ["Name"] = null });

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Null(entity.RtDisplayName);
    }

    [Fact]
    public async Task Run_NoRule_LeavesNull()
    {
        var (modifier, session, dataSource) = CreateModifier(null);
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich" });
        entity.RtDisplayName = "stale value";

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Null(entity.RtDisplayName);
    }

    [Fact]
    public async Task Run_BothRules_SetsBothFields()
    {
        var (modifier, session, dataSource) = CreateModifier("${Name}", "${Description ?? Name}");
        var entity = CreateEntity(new() { ["Name"] = "Wohnbereich", ["Description"] = null });

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Equal("Wohnbereich", entity.RtDisplayName);
        Assert.Equal("Wohnbereich", entity.RtDisplayDescription);
    }

    [Fact]
    public async Task Run_MissingAttribute_TreatedAsEmpty()
    {
        var (modifier, session, dataSource) = CreateModifier("${Name ?? GlobalId}");
        var entity = CreateEntity(new());

        await modifier.RunAsync(session, dataSource, [entity]);

        Assert.Null(entity.RtDisplayName);
    }
}
