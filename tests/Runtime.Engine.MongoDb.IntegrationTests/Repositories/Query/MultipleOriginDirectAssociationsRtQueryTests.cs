using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
/// Tests for <see cref="MultipleOriginDirectAssociationsRtQuery{TTargetEntity}"/>
/// focusing on correct totalCount calculation when pagination ($limit) is used.
/// </summary>
[Collection("Sequential")]
public class MultipleOriginDirectAssociationsRtQueryTests
    : IClassFixture<SampleRtModelDataFixture>
{
    private readonly SampleRtModelDataFixture _fixture;

    public MultipleOriginDirectAssociationsRtQueryTests(SampleRtModelDataFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    [Fact]
    public async Task Sorted_Paginated_TotalCount_ReflectsRealTotal()
    {
        // Arrange: Salzburg has 6 non-archived districts.
        // With sort+take, the OPTIMIZED path is used which includes $limit inside the pipeline.
        // totalCount must still reflect the real total (6), not the limited page size.
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var salzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");

        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Descending);

        // Act: Take only 2 of the 6 districts
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [salzburgRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 2);

        // Assert
        Assert.Single(result);
        var salzburg = result.First().Value;
        Assert.Equal(6, salzburg.TotalCount); // Real total, not limited to 3 (=0+2+1)
        Assert.Equal(2, salzburg.Items.Count());
    }

    [Fact]
    public async Task Sorted_Paginated_WithSkip_TotalCount_ReflectsRealTotal()
    {
        // Arrange: Same scenario but with skip to test the second page
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var salzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");

        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Ascending);

        // Act: Skip 2, take 2 (second page of 6 districts)
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [salzburgRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions, 2, 2);

        // Assert
        Assert.Single(result);
        var salzburg = result.First().Value;
        Assert.Equal(6, salzburg.TotalCount); // Real total regardless of skip/take
        Assert.Equal(2, salzburg.Items.Count());
    }

    [Fact]
    public async Task Sorted_TakeOnly_TotalCount_ReflectsRealTotal()
    {
        // Arrange: Take without skip (null skip triggers different $replaceWith branch)
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var salzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");

        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Descending);

        // Act: Take 3 without skip
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [salzburgRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions, null, 3);

        // Assert
        Assert.Single(result);
        var salzburg = result.First().Value;
        Assert.Equal(6, salzburg.TotalCount);
        Assert.Equal(3, salzburg.Items.Count());
    }

    [Fact]
    public async Task Unsorted_Paginated_TotalCount_ReflectsRealTotal()
    {
        // Arrange: Without sort, the STANDARD path is used.
        // $limit is still applied but via $setWindowFields the total should be preserved.
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var salzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");

        var queryOptions = RtEntityQueryOptions.Create();

        // Act: Take 2 without sort (standard path)
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [salzburgRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 2);

        // Assert
        Assert.Single(result);
        var salzburg = result.First().Value;
        Assert.Equal(6, salzburg.TotalCount);
        Assert.Equal(2, salzburg.Items.Count());
    }

    [Fact]
    public async Task MultipleOrigins_Sorted_Paginated_TotalCount_PerOrigin()
    {
        // Arrange: Multiple origins with different child counts.
        // Salzburg: 6 districts, Tirol: 2 non-archived districts (4 total, 2 archived).
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var salzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");
        var tirolRtId = OctoObjectId.Parse("68fded922b85e5d74c05a560");

        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Ascending);

        // Act: Take 1 per origin with sort (optimized path)
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [salzburgRtId, tirolRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 1);

        // Assert: Each origin should have correct totalCount
        Assert.Equal(2, result.Count);

        Assert.True(result.TryGetValue(new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, salzburgRtId), out var salzburg));
        Assert.Equal(6, salzburg!.TotalCount);
        Assert.Single(salzburg.Items);

        Assert.True(result.TryGetValue(new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, tirolRtId), out var tirol));
        Assert.Equal(2, tirol!.TotalCount); // 2 non-archived
        Assert.Single(tirol.Items);
    }

    [Fact]
    public async Task NoPagination_TotalCount_EqualsItemCount()
    {
        // Arrange: Without pagination, totalCount should equal the number of returned items.
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var salzburgRtId = OctoObjectId.Parse("66803ecf4aa85720dda96a99");

        var queryOptions = RtEntityQueryOptions.Create();

        // Act: No skip/take
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [salzburgRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions);

        // Assert
        Assert.Single(result);
        var salzburg = result.First().Value;
        Assert.Equal(6, salzburg.TotalCount);
        Assert.Equal(salzburg.TotalCount, salzburg.Items.Count());
    }

    [Fact]
    public async Task Sorted_Paginated_ArchivedExcluded_TotalCount_ReflectsNonArchived()
    {
        // Arrange: Tirol has 4 districts total, 2 archived (Imst, Kitzbühel).
        // TotalCount should reflect only non-archived: 2.
        var systemContext = _fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var tirolRtId = OctoObjectId.Parse("68fded922b85e5d74c05a560");

        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Ascending);

        // Act: Take 1 of 2 non-archived districts
        var result = await tenantRepository.GetRtAssociationTargetsAsync(session,
            [tirolRtId], TestCkIds.RtCkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 1);

        // Assert
        Assert.Single(result);
        var tirol = result.First().Value;
        Assert.Equal(2, tirol.TotalCount); // Only non-archived districts
        Assert.Single(tirol.Items);
    }
}
