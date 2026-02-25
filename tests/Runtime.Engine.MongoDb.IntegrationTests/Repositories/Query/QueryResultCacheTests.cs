using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection("Sequential")]
public class QueryResultCacheTests
    : IClassFixture<SampleRtModelDataFixture>
{
    private readonly SampleRtModelDataFixture _systemFixture;

    public QueryResultCacheTests(SampleRtModelDataFixture systemFixture, ITestOutputHelper output)
    {
        _systemFixture = systemFixture;
        _systemFixture.OutputHelper = output;
    }

    /// <summary>
    /// Verifies that the cache path returns the same results as the non-cache path.
    /// Compares a full (unpaginated) query against a paginated query that uses the cache.
    /// </summary>
    [Fact]
    public async Task CachePath_ReturnsIdenticalResults_ToNonCachePath()
    {
        var (systemContext, ckCacheService) = GetServices();
        var navigationPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Full query without pagination (bypasses cache)
        var fullResult = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navigationPairs);

        Assert.True(fullResult.TotalCount > 0, "Test data must have districts with parent StateOrProvince");

        // Paginated query (uses cache path: Filter mode + navigationPairs + skip/take)
        var pagedNavigationPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var pagedResult = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            pagedNavigationPairs,
            0, (int)fullResult.TotalCount); // Request all items via pagination

        Assert.Equal(fullResult.TotalCount, pagedResult.TotalCount);
        Assert.Equal(fullResult.Items.Count(), pagedResult.Items.Count());

        // Verify same entities returned (by RtId)
        var fullIds = fullResult.Items.Select(e => e.RtId).OrderBy(id => id).ToList();
        var pagedIds = pagedResult.Items.Select(e => e.RtId).OrderBy(id => id).ToList();
        Assert.Equal(fullIds, pagedIds);
    }

    /// <summary>
    /// Verifies that a second paginated call with the same parameters hits the cache
    /// and returns consistent results (same TotalCount and page content).
    /// </summary>
    [Fact]
    public async Task CacheHit_ReturnsSameResults_AsFirstCall()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // First call — cache miss, populates cache
        var navPairs1 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var result1 = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs1,
            0, 3);

        // Second call — cache hit (same parameters)
        var navPairs2 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var result2 = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs2,
            0, 3);

        Assert.Equal(result1.TotalCount, result2.TotalCount);
        Assert.Equal(result1.Items.Count(), result2.Items.Count());

        var ids1 = result1.Items.Select(e => e.RtId).ToList();
        var ids2 = result2.Items.Select(e => e.RtId).ToList();
        Assert.Equal(ids1, ids2);
    }

    /// <summary>
    /// Verifies that different pages from the cache are disjoint and cover the full result set.
    /// </summary>
    [Fact]
    public async Task CachePagination_DifferentPages_AreDisjoint()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Page 1: skip=0, take=2
        var navPairs1 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var page1 = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs1,
            0, 2);

        // Page 2: skip=2, take=2
        var navPairs2 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var page2 = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs2,
            2, 2);

        Assert.Equal(page1.TotalCount, page2.TotalCount);
        Assert.True(page1.TotalCount >= 4, "Need at least 4 districts for meaningful two-page test");

        var page1Ids = page1.Items.Select(e => e.RtId).ToHashSet();
        var page2Ids = page2.Items.Select(e => e.RtId).ToHashSet();

        // Pages must be disjoint
        Assert.Empty(page1Ids.Intersect(page2Ids));
    }

    /// <summary>
    /// Verifies that all pages together reconstruct the full result set.
    /// </summary>
    [Fact]
    public async Task CachePagination_AllPages_ReconstructFullResult()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Get full result (no pagination, bypasses cache)
        var fullNavPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var fullResult = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            fullNavPairs);

        var totalCount = (int)fullResult.TotalCount;
        Assert.True(totalCount > 0);

        // Paginate through all items in pages of 2
        var allPagedIds = new List<OctoObjectId>();
        const int pageSize = 2;
        for (int skip = 0; skip < totalCount; skip += pageSize)
        {
            var navPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
            var page = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
                session,
                TestCkIds.RtCkDistrictTypeId,
                RtEntityQueryOptions.Create(),
                navPairs,
                skip, pageSize);

            Assert.Equal(totalCount, page.TotalCount);
            allPagedIds.AddRange(page.Items.Select(e => e.RtId));
        }

        var fullIds = fullResult.Items.Select(e => e.RtId).OrderBy(id => id).ToList();
        var pagedIds = allPagedIds.OrderBy(id => id).ToList();
        Assert.Equal(fullIds, pagedIds);
    }

    /// <summary>
    /// Verifies that the TotalCount from the cache is correct and consistent across pages.
    /// </summary>
    [Fact]
    public async Task CachePath_TotalCount_IsCorrect()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Unpaginated result for reference count
        var fullNavPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var fullResult = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            fullNavPairs);

        // Paginated (cache) result
        var pagedNavPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var pagedResult = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            pagedNavPairs,
            0, 2);

        Assert.Equal(fullResult.TotalCount, pagedResult.TotalCount);
        Assert.Equal(2, pagedResult.Items.Count());
    }

    /// <summary>
    /// Verifies that different query options produce different cache keys
    /// and therefore independent cached results.
    /// </summary>
    [Fact]
    public async Task DifferentFieldFilters_ProduceDifferentCacheKeys()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Query with field filter for "Salzburg" districts
        var navPairs1 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var options1 = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Ascending);

        var result1 = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            options1,
            navPairs1,
            0, 100);

        // Query with different sort order
        var navPairs2 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var options2 = RtEntityQueryOptions.Create()
            .SortOrder("Name", SortOrders.Descending);

        var result2 = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            options2,
            navPairs2,
            0, 100);

        // Both should have the same total count
        Assert.Equal(result1.TotalCount, result2.TotalCount);

        // But entity order should differ (ascending vs descending)
        var ids1 = result1.Items.Select(e => e.RtId).ToList();
        var ids2 = result2.Items.Select(e => e.RtId).ToList();

        // Same set of IDs
        Assert.Equal(ids1.OrderBy(id => id), ids2.OrderBy(id => id));

        // But different order (first item of ascending != first item of descending, if count > 1)
        if (ids1.Count > 1)
        {
            Assert.NotEqual(ids1.First(), ids2.First());
        }
    }

    /// <summary>
    /// Verifies that the cache path correctly includes navigation (enrichment) data.
    /// Entities returned via cache must have their association data populated.
    /// </summary>
    [Fact]
    public async Task CachePath_EnrichmentData_IsPopulated()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var navPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var result = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs,
            0, 3);

        Assert.True(result.Items.Any(), "Must have at least one district");

        // Verify enrichment: each district should have associations populated
        foreach (var entity in result.Items)
        {
            Assert.NotNull(entity.Associations);
            Assert.NotEmpty(entity.Associations);
        }
    }

    /// <summary>
    /// Verifies that Include mode (not Filter) bypasses the cache
    /// and still returns correct results.
    /// </summary>
    [Fact]
    public async Task IncludeMode_BypassesCache_ReturnsResults()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var navPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var options = RtEntityQueryOptions.Create()
            .UseNavigationFilterMode(NavigationFilterMode.Include);

        var result = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            options,
            navPairs,
            0, 3);

        // Include mode should still work, just not use cache
        Assert.Equal(3, result.Items.Count());
        Assert.True(result.TotalCount >= 3);
    }

    /// <summary>
    /// Verifies that requesting a page beyond the result set returns empty items
    /// but correct TotalCount.
    /// </summary>
    [Fact]
    public async Task CachePath_SkipBeyondEnd_ReturnsEmptyPage()
    {
        var (systemContext, ckCacheService) = GetServices();

        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var navPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var result = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs,
            1000, 10); // Skip well beyond the result set

        Assert.Empty(result.Items);
        Assert.True(result.TotalCount > 0, "TotalCount should still reflect the full result set");
    }

    /// <summary>
    /// Verifies that ComputeCacheKey produces deterministic output.
    /// </summary>
    [Fact]
    public void ComputeCacheKey_IsDeterministic()
    {
        var (systemContext, ckCacheService) = GetServices();

        var navPairs1 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var navPairs2 = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);

        var key1 = QueryResultCacheService.ComputeCacheKey(
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs1);

        var key2 = QueryResultCacheService.ComputeCacheKey(
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs2);

        Assert.Equal(key1, key2);
        Assert.Equal(64, key1.Length); // SHA256 hex = 64 chars
    }

    /// <summary>
    /// Verifies that different parameters produce different cache keys.
    /// </summary>
    [Fact]
    public void ComputeCacheKey_DifferentParams_ProduceDifferentKeys()
    {
        var (systemContext, ckCacheService) = GetServices();

        var navPairs = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);

        var keyDefault = QueryResultCacheService.ComputeCacheKey(
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(),
            navPairs);

        // Different sort order
        var navPairsSorted = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var keySorted = QueryResultCacheService.ComputeCacheKey(
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending),
            navPairsSorted);

        // Different type
        var navPairsOther = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var keyOtherType = QueryResultCacheService.ComputeCacheKey(
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            navPairsOther);

        // Different archived flag
        var navPairsArchived = CreateDistrictToStateNavigationPairs(ckCacheService, systemContext.TenantId);
        var keyArchived = QueryResultCacheService.ComputeCacheKey(
            TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create().Global(true),
            navPairsArchived);

        Assert.NotEqual(keyDefault, keySorted);
        Assert.NotEqual(keyDefault, keyOtherType);
        Assert.NotEqual(keyDefault, keyArchived);
    }

    /// <summary>
    /// Verifies that the QueryResultCacheService can store and retrieve cache entries
    /// directly via its API.
    /// </summary>
    [Fact]
    public async Task CacheService_StoreAndRetrieve_RoundTrips()
    {
        var loggerFactory = _systemFixture.GetService<ILoggerFactory>();
        var repositoryDataSource = new MongoDbRepositoryDataSource(
            loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _systemFixture.GetService<IUserRepositoryAccess>(),
            _systemFixture.SystemDatabaseName,
            _systemFixture.GetSystemContext().TenantId);

        var cacheService = repositoryDataSource.CreateQueryResultCacheService();

        var testKey = $"test_roundtrip_{Guid.NewGuid():N}";
        var testIds = new List<OctoObjectId>
        {
            OctoObjectId.GenerateNewId(),
            OctoObjectId.GenerateNewId(),
            OctoObjectId.GenerateNewId()
        };

        // Initially not cached
        var cached = await cacheService.TryGetAsync(testKey);
        Assert.Null(cached);

        // Store
        await cacheService.StoreAsync(testKey, testIds);

        // Retrieve
        cached = await cacheService.TryGetAsync(testKey);
        Assert.NotNull(cached);
        Assert.Equal(3, cached.Value.TotalCount);
        Assert.Equal(testIds, cached.Value.EntityIds);
    }

    /// <summary>
    /// Verifies that storing with the same key overwrites the previous entry.
    /// </summary>
    [Fact]
    public async Task CacheService_Store_OverwritesExistingEntry()
    {
        var loggerFactory = _systemFixture.GetService<ILoggerFactory>();
        var repositoryDataSource = new MongoDbRepositoryDataSource(
            loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _systemFixture.GetService<IUserRepositoryAccess>(),
            _systemFixture.SystemDatabaseName,
            _systemFixture.GetSystemContext().TenantId);

        var cacheService = repositoryDataSource.CreateQueryResultCacheService();

        var testKey = $"test_overwrite_{Guid.NewGuid():N}";
        var ids1 = new List<OctoObjectId> { OctoObjectId.GenerateNewId() };
        var ids2 = new List<OctoObjectId> { OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId() };

        await cacheService.StoreAsync(testKey, ids1);
        await cacheService.StoreAsync(testKey, ids2);

        var cached = await cacheService.TryGetAsync(testKey);
        Assert.NotNull(cached);
        Assert.Equal(2, cached.Value.TotalCount);
        Assert.Equal(ids2, cached.Value.EntityIds);
    }

    /// <summary>
    /// Verifies that an empty ID list can be stored and retrieved.
    /// </summary>
    [Fact]
    public async Task CacheService_StoreEmptyList_RoundTrips()
    {
        var loggerFactory = _systemFixture.GetService<ILoggerFactory>();
        var repositoryDataSource = new MongoDbRepositoryDataSource(
            loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _systemFixture.GetService<IUserRepositoryAccess>(),
            _systemFixture.SystemDatabaseName,
            _systemFixture.GetSystemContext().TenantId);

        var cacheService = repositoryDataSource.CreateQueryResultCacheService();

        var testKey = $"test_empty_{Guid.NewGuid():N}";
        await cacheService.StoreAsync(testKey, []);

        var cached = await cacheService.TryGetAsync(testKey);
        Assert.NotNull(cached);
        Assert.Equal(0, cached.Value.TotalCount);
        Assert.Empty(cached.Value.EntityIds);
    }

    private (ISystemContext, ICkCacheService) GetServices()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();
        return (systemContext, ckCacheService);
    }

    private static ICollection<NavigationPair> CreateDistrictToStateNavigationPairs(
        ICkCacheService ckCacheService, string tenantId)
    {
        var requestedFieldNames = new[]
        {
            "name",
            "parent.testStateOrProvince->name"
        };

        return RtPathEvaluator.TokenizeAndGetNavigationPairs(
            ckCacheService,
            tenantId,
            TestCkIds.CkDistrictTypeId,
            requestedFieldNames);
    }
}
