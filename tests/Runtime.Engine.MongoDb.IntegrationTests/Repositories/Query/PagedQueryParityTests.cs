using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     Page parity (AB#8896): for every query shape of <see cref="CountParityCases" /> the paged item query returns the
///     items and the total count of the unpaged item query. The unpaged query runs neither a <c>$facet</c> nor a
///     separate count, so it is the reference for the old paged implementation (count and page in one <c>$facet</c>)
///     and for the new one (separate count and page pipelines) alike. With a sort order (rtId added to the shape) every
///     window holds exactly the items of the same window of the unpaged query, in the same order. Without one the order
///     of <c>$skip</c>/<c>$limit</c> is not defined (no tiebreak, old and new alike), so only what does not depend on
///     the order is checked: the total count, the page size and that every page item is an entity of the unpaged
///     query. Written and green before the change; it stays unchanged.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class PagedQueryParityTests(SampleRtModelDataFixture fixture)
{
    /// <summary>The paging windows (skip, take) every shape is checked with, including one beyond the end.</summary>
    internal static readonly (int Skip, int Take)[] Windows = [(0, 1), (0, 2), (1, 2), (2, 100), (100, 10)];

    [Theory]
    [MemberData(nameof(CountParityCases.Names), MemberType = typeof(CountParityCases))]
    public async Task PagedQuery_SortedByRtId_ReturnsTheWindowOfTheUnpagedQuery(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var unpaged = await QueryAsync(repository, session, c, sortByRtId: true);
        Assert.Equal(unpaged.Ids.Count, unpaged.TotalCount);
        Assert.True(unpaged.TotalCount >= c.MinExpected, $"{caseName}: {unpaged.TotalCount} < {c.MinExpected}");

        foreach (var (skip, take) in Windows)
        {
            var paged = await QueryAsync(repository, session, c, sortByRtId: true, skip, take);

            Assert.Equal(unpaged.TotalCount, paged.TotalCount);
            Assert.Equal(unpaged.Ids.Skip(skip).Take(take).ToList(), paged.Ids);
        }

        await session.CommitTransactionAsync();
    }

    [Theory]
    [MemberData(nameof(CountParityCases.Names), MemberType = typeof(CountParityCases))]
    public async Task PagedQuery_Unsorted_ReturnsEntitiesOfTheUnpagedQueryAndItsTotal(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var unpaged = await QueryAsync(repository, session, c, sortByRtId: false);
        var unpagedIds = unpaged.Ids.ToHashSet();
        foreach (var (skip, take) in Windows)
        {
            var page = await QueryAsync(repository, session, c, sortByRtId: false, skip, take);

            Assert.Equal(unpaged.TotalCount, page.TotalCount);
            Assert.Equal(Math.Clamp(unpaged.Ids.Count - skip, 0, take), page.Ids.Count);
            Assert.Equal(page.Ids.Count, page.Ids.Distinct().Count());
            Assert.All(page.Ids, id => Assert.Contains(id, unpagedIds));
        }

        await session.CommitTransactionAsync();
    }

    /// <summary>The item ids (in result order) and the total count of the old item query of a shape.</summary>
    private async Task<(IReadOnlyList<OctoObjectId> Ids, long TotalCount)> QueryAsync(ITenantRepository repository,
        IOctoSession session, CountParityCases.Case c, bool sortByRtId, int? skip = null, int? take = null)
    {
        var options = c.Options();
        if (sortByRtId)
        {
            options.SortOrder("rtId", SortOrders.Ascending);
        }

        if (c.Navigation == null)
        {
            var result = await repository.GetRtEntitiesByTypeAsync(session, c.CkTypeId, options, skip, take);
            return (result.Items.Select(e => e.RtId).ToList(), result.TotalCount);
        }

        // Caching disabled: the paged graph query must run the item query, not serve ids from the result cache.
        var graphResult = await repository.GetRtEntitiesGraphByTypeAsync(session, c.CkTypeId,
            options.WithCachingDisabled(), c.Navigation(fixture), skip, take);
        return (graphResult.Items.Select(e => e.RtId).ToList(), graphResult.TotalCount);
    }
}

/// <summary>
///     Page parity under data permissions (AB#8896): per caller of <see cref="CountParityDataPermissionTests" />, a
///     page holds the window of the unpaged query and reports the count the caller sees. Written and green before the
///     change; it stays unchanged.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class PagedQueryParityDataPermissionTests(DataPermissionTestFixture fixture)
{
    [Theory]
    [MemberData(nameof(CountParityDataPermissionTests.Callers), MemberType = typeof(CountParityDataPermissionTests))]
    public async Task PagedQuery_ReturnsTheWindowTheCallerSees(string caller, long expected, string? nameFilter)
    {
        var repository = await CountParityDataPermissionTests.SeedAsync(fixture);
        fixture.Resolver.Table = CountParityDataPermissionTests.BuildTable();
        try
        {
            using var session = await CountParityDataPermissionTests.OpenSessionAsync(repository, caller);
            session.StartTransaction();

            var unpaged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                CountParityDataPermissionTests.Options(nameFilter).SortOrder("Name", SortOrders.Ascending));
            var unpagedIds = unpaged.Items.Select(e => e.RtId).ToList();
            Assert.Equal(expected, unpaged.TotalCount);

            foreach (var (skip, take) in PagedQueryParityTests.Windows)
            {
                var paged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                    CountParityDataPermissionTests.Options(nameFilter).SortOrder("Name", SortOrders.Ascending),
                    skip, take);

                Assert.Equal(expected, paged.TotalCount);
                Assert.Equal(unpagedIds.Skip(skip).Take(take).ToList(), paged.Items.Select(e => e.RtId).ToList());
            }

            await session.CommitTransactionAsync();
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }
}
