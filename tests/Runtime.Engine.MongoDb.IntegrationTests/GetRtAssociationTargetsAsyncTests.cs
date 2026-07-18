using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(SampleRtModelDataCollection.Name)]
public class GetRtAssociationTargetsAsyncTests
{
    private readonly SampleRtModelDataFixture _sampleRtModelDataFixture;

    public GetRtAssociationTargetsAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture, ITestOutputHelper output)
    {
        _sampleRtModelDataFixture = sampleRtModelDataFixture;
        sampleRtModelDataFixture.OutputHelper = output;
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            queryOptions, 0, 5);

        var rtIds = result.Items.Select(x => x.RtId).ToList();
        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, rtIds, TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 5);

        Assert.Equal(5, deep.Count);
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_Deleted_DefaultDoNotIncludeDeleted_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create();

        var rtEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(TestCkIds.RtCkDistrictTypeId, new OctoObjectId("68fded922b85e5d74c05a564")));
        Assert.NotNull(rtEntity);

        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, [rtEntity.RtId], TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 5);

        Assert.Single(deep);
        Assert.NotNull(deep[rtEntity.ToRtEntityId()]);
        Assert.Equal(0, deep[rtEntity.ToRtEntityId()].TotalCount);
    }


    [Fact]
    public async Task GetRtAssociationTargetsAsync_FromDeleted_IncludeDeletedAssocs_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create().Global(true);

        var rtEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(TestCkIds.RtCkDistrictTypeId, new OctoObjectId("68fded922b85e5d74c05a564")));
        Assert.NotNull(rtEntity);

        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, [rtEntity.RtId], TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 5);

        Assert.Single(deep);
        Assert.NotNull(deep[rtEntity.ToRtEntityId()]);
        Assert.Equal(2, deep[rtEntity.ToRtEntityId()].TotalCount);
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_MultipleTargetTypes_OK()
    {
        // Arrange
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        // Get StateOrProvince entities (Salzburg, Tirol)
        var stateProvinceResult = await tenantRepository.GetRtEntitiesByTypeAsync(session,
            TestCkIds.RtCkStateOrProvinceTypeId, queryOptions, 0, 2);
        Assert.True(stateProvinceResult.Items.Any(), "No StateOrProvince entities found");

        var stateProvinceRtIds = stateProvinceResult.Items.Select(x => x.RtId).ToList();

        // Act - Query for multiple target types (District AND Municipality)
        var multipleTargetTypes = new[]
        {
            TestCkIds.RtCkDistrictTypeId,
            TestCkIds.RtCkMunicipalityTypeId
        };

        var result = await tenantRepository.GetRtAssociationTargetsAsync(
            session,
            stateProvinceRtIds,
            TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            multipleTargetTypes,
            GraphDirections.Inbound,
            null,
            queryOptions);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count > 0, "Expected results for multiple target types query");

        // Verify we get results (Districts and/or Municipalities)
        var totalResults = result.Sum(r => r.Value.TotalCount);
        Assert.True(totalResults > 0, "Expected to find at least some child entities");
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_MultipleTargetTypes_SingleOrigin_OK()
    {
        // Arrange
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        // Get first District
        var districtResult = await tenantRepository.GetRtEntitiesByTypeAsync(session,
            TestCkIds.RtCkDistrictTypeId, queryOptions, 0, 1);
        Assert.True(districtResult.Items.Any(), "No District entities found");

        var districtRtId = districtResult.Items.First().RtId;

        // Act - Query for single target type (Municipality only)
        var singleTargetResult = await tenantRepository.GetRtAssociationTargetsAsync(
            session,
            [districtRtId],
            TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId,
            GraphDirections.Inbound,
            null,
            queryOptions);

        // Query with multiple target types (only Municipality, but as array)
        var multipleTargetResult = await tenantRepository.GetRtAssociationTargetsAsync(
            session,
            [districtRtId],
            TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            [TestCkIds.RtCkMunicipalityTypeId],
            GraphDirections.Inbound,
            null,
            queryOptions);

        // Assert - Both queries should return the same results
        Assert.Equal(singleTargetResult.Count, multipleTargetResult.Count);

        foreach (var kvp in singleTargetResult)
        {
            Assert.True(multipleTargetResult.TryGetValue(kvp.Key, out var multiResult));
            Assert.Equal(kvp.Value.TotalCount, multiResult.TotalCount);
        }
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_Household_MultipleChildTypes_OK()
    {
        // Arrange
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        // Household "Zeller Fusch 153" - has children: Room (Kitchen), TechnicalRoom, MeasuringPoint
        var householdRtId = new OctoObjectId("66803ecf4aa85720dda96b09");

        // Expected child entity IDs
        var expectedRoomId = new OctoObjectId("68fded922b85e5d74c05a567");        // Kitchen
        var expectedTechnicalRoomId = new OctoObjectId("68fded922b85e5d74c05a568"); // Technical Room Ground Floor
        var expectedMeasuringPointId = new OctoObjectId("66803ecf4aa85720dda96b11"); // Hauptzähler

        // Act - Query for all child types (Room, TechnicalRoom, MeasuringPoint)
        var result = await tenantRepository.GetRtAssociationTargetsAsync(
            session,
            [householdRtId],
            TestCkIds.RtCkHouseHoldTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            [TestCkIds.RtCkRoomTypeId, TestCkIds.RtCkTechnicalRoomTypeId, TestCkIds.RtCkMeasuringPointTypeId],
            GraphDirections.Inbound,
            null,
            queryOptions);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result); // One origin (the household)

        var householdEntityId = new RtEntityId(TestCkIds.RtCkHouseHoldTypeId, householdRtId);
        Assert.True(result.TryGetValue(householdEntityId, out var children));

        // Should find all 3 children: Room, TechnicalRoom, MeasuringPoint
        Assert.Equal(3, children.TotalCount);

        var childRtIds = children.Items.Select(x => x.RtId).ToList();
        Assert.Contains(expectedRoomId, childRtIds);
        Assert.Contains(expectedTechnicalRoomId, childRtIds);
        Assert.Contains(expectedMeasuringPointId, childRtIds);
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_Any_ReturnsUnionOfInboundAndOutbound()
    {
        // Arrange
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        // Household "Zeller Fusch 153" sits in the MIDDLE of the ParentChild graph: it has three
        // children (Room, TechnicalRoom, MeasuringPoint) reachable INBOUND and is itself the child
        // of its Municipality, reachable OUTBOUND. GraphDirections.Any must return the union of both.
        var householdRtId = new OctoObjectId("66803ecf4aa85720dda96b09");
        var householdEntityId = new RtEntityId(TestCkIds.RtCkHouseHoldTypeId, householdRtId);

        // Target types broad enough to cover both the children and the parent municipality.
        RtCkId<CkTypeId>[] targetTypes =
        [
            TestCkIds.RtCkRoomTypeId,
            TestCkIds.RtCkTechnicalRoomTypeId,
            TestCkIds.RtCkMeasuringPointTypeId,
            TestCkIds.RtCkMunicipalityTypeId
        ];

        // Act
        var inbound = await tenantRepository.GetRtAssociationTargetsAsync(session, [householdRtId],
            TestCkIds.RtCkHouseHoldTypeId, SystemCkIds.RtCkParentChildRoleId, targetTypes,
            GraphDirections.Inbound, null, queryOptions);
        var outbound = await tenantRepository.GetRtAssociationTargetsAsync(session, [householdRtId],
            TestCkIds.RtCkHouseHoldTypeId, SystemCkIds.RtCkParentChildRoleId, targetTypes,
            GraphDirections.Outbound, null, queryOptions);
        // Previously threw GraphDirectionUnsupported — now returns the union.
        var any = await tenantRepository.GetRtAssociationTargetsAsync(session, [householdRtId],
            TestCkIds.RtCkHouseHoldTypeId, SystemCkIds.RtCkParentChildRoleId, targetTypes,
            GraphDirections.Any, null, queryOptions);

        // Assert
        Assert.True(inbound.TryGetValue(householdEntityId, out var inSet));
        Assert.True(outbound.TryGetValue(householdEntityId, out var outSet));
        Assert.True(any.TryGetValue(householdEntityId, out var anySet));

        Assert.Equal(3, inSet!.TotalCount); // the three children
        Assert.True(outSet!.TotalCount >= 1, "household must have a ParentChild parent (outbound edge)");

        // Union: counts sum (directed edges are disjoint) and the item id set is the union.
        Assert.Equal(inSet.TotalCount + outSet.TotalCount, anySet!.TotalCount);

        var expectedIds = inSet.Items.Select(x => x.RtId)
            .Concat(outSet.Items.Select(x => x.RtId))
            .ToHashSet();
        var actualIds = anySet.Items.Select(x => x.RtId).ToHashSet();
        Assert.Equal(expectedIds, actualIds);
    }
}
