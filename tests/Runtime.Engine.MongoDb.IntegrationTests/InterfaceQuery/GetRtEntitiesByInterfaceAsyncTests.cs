using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>
///     <c>ITenantRepository.GetRtEntitiesByInterfaceAsync</c> (AB#7019) against real MongoDB: the entities of all
///     implementers of a CK interface over several collections, filtered, sorted, paged and counted in one global
///     order. Expectations are computed by hand from <see cref="InterfaceQuerySeed.SeedStandardAsync" />.
/// </summary>
[Collection(InterfaceQueryCollection.Name)]
public class GetRtEntitiesByInterfaceAsyncTests(InterfaceQueryFixture fixture)
{
    private static readonly RtCkId<CkInterfaceId> Named = new("InterfaceQuery/Named");
    private static readonly RtCkId<CkInterfaceId> Titled = new("InterfaceQuery/Titled");
    private static readonly RtCkId<CkInterfaceId> Lonely = new("InterfaceQuery/Lonely");

    private async Task<ITenantRepository> SeedAsync()
    {
        await fixture.ResetAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        await InterfaceQuerySeed.SeedStandardAsync(repository);
        return repository;
    }

    private static async Task<RtInterfaceQueryResult> QueryAsync(ITenantRepository repository,
        RtCkId<CkInterfaceId> ckInterfaceId, RtEntityQueryOptions options, int skip, int take, bool count,
        bool inTransaction, RtSecurityContext? securityContext = null)
    {
        using var session = securityContext == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(securityContext);
        if (inTransaction)
        {
            session.StartTransaction();
        }

        var result = await repository.GetRtEntitiesByInterfaceAsync(session, ckInterfaceId, options, skip, take, count);
        if (inTransaction)
        {
            await session.CommitTransactionAsync();
        }

        return result;
    }

    // ---- Result: global order, paging, count, identical for every collection layout -------------------------------

    // Name ascending over Asset+Pump (one collection), Sensor+Gauge (one), Valve, Vault: the ties alpha (1 | 7) and
    // bravo (3 | 9) are across collections and break by rtId.
    private static readonly int[] NameAscending = [10, 1, 7, 3, 9, 5, 2, 4, 6, 8];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryWindowOfTheGlobalOrder_EqualsTheHandComputedExpectation(bool inTransaction)
    {
        var repository = await SeedAsync();

        for (var skip = 0; skip <= NameAscending.Length; skip++)
        {
            for (var take = 1; take <= 4; take++)
            {
                var result = await QueryAsync(repository, Named,
                    RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), skip, take, true,
                    inTransaction);

                Assert.Equal(NameAscending.Skip(skip).Take(take), InterfaceQuerySeed.Numbers(result.Items));
                Assert.Equal(NameAscending.Length, result.TotalCount);
            }
        }
    }

    [Fact]
    public async Task Descending_SortOnANumericMember_BreaksTiesByRtId()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Named,
            RtEntityQueryOptions.Create().SortOrder("Rank", SortOrders.Descending), 0, 20, true, false);

        // Rank: 6=9, 9=8, 7=7, 1=5, 8=4, then 2 and 3 (both 3, by rtId), 5=2, 4=1, 10=0
        Assert.Equal(new[] { 6, 9, 7, 1, 8, 2, 3, 5, 4, 10 }, InterfaceQuerySeed.Numbers(result.Items));
    }

    [Fact]
    public async Task SecondarySortKey_OrdersTheTies()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Named,
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending).SortOrder("Rank", SortOrders.Descending),
            0, 20, false, true);

        // alpha: 7 (rank 7) before 1 (rank 5); bravo: 9 (rank 8) before 3 (rank 3)
        Assert.Equal(new[] { 10, 7, 1, 9, 3, 5, 2, 4, 6, 8 }, InterfaceQuerySeed.Numbers(result.Items));
    }

    [Fact]
    public async Task WithoutSort_TheOrderIsRtIdAscending()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Named, RtEntityQueryOptions.Create(), 2, 5, true, false);

        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, InterfaceQuerySeed.Numbers(result.Items));
        Assert.Equal(10, result.TotalCount);
    }

    [Fact]
    public async Task FilterOnAnInterfaceMember_IsAppliedInEveryCollection_AndCounted()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Named,
            RtEntityQueryOptions.Create().FieldFilter("Rank", FieldFilterOperator.GreaterEqualThan, 4)
                .SortOrder("Name", SortOrders.Ascending), 1, 3, true, false);

        // Rank >= 4: 1(alpha), 6(foxtrot), 7(alpha), 8(golf), 9(bravo), 10 is 0 -> by name: 1, 7, 9, 6, 8
        Assert.Equal(new[] { 7, 9, 6 }, InterfaceQuerySeed.Numbers(result.Items));
        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task OneCollection_GivesTheSameAnswerAsTheTypeQuery()
    {
        var repository = await SeedAsync();

        // Titled is implemented by Asset and (derived) Pump only: one collection.
        var result = await QueryAsync(repository, Titled,
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 10, true, false);
        Assert.Equal(new[] { 1, 3, 2, 4 }, InterfaceQuerySeed.Numbers(result.Items));
        Assert.Equal(4, result.TotalCount);

        using var session = await repository.GetSessionAsync();
        var typeResult = await repository.GetRtEntitiesByTypeAsync(session, new RtCkId<CkTypeId>("InterfaceQuery/Asset"),
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 10);
        Assert.Equal(InterfaceQuerySeed.Numbers(typeResult.Items), InterfaceQuerySeed.Numbers(result.Items));
        Assert.Equal(typeResult.TotalCount, result.TotalCount);
    }

    [Fact]
    public async Task ArchivedEntities_AreExcludedUnlessRequested()
    {
        var repository = await SeedAsync();

        var withArchived = await QueryAsync(repository, Titled,
            RtEntityQueryOptions.Create().Global(true).SortOrder("Name", SortOrders.Ascending), 0, 10, true, false);

        Assert.Equal(new[] { 1, 3, 2, 4, 11 }, InterfaceQuerySeed.Numbers(withArchived.Items));
        Assert.Equal(5, withArchived.TotalCount);
    }

    [Fact]
    public async Task ReturnedEntities_AreCompleteAndNameTheirConcreteType()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Named,
            RtEntityQueryOptions.Create().FieldFilter("Rank", FieldFilterOperator.Equals, 7), 0, 5, false, true);

        var gauge = Assert.Single(result.Items);
        Assert.Equal("InterfaceQuery/Gauge", gauge.CkTypeId!.SemanticVersionedFullName);
        Assert.Equal("alpha", InterfaceQuerySeed.NameOf(gauge));
        Assert.Equal(InterfaceQuerySeed.Id(7), gauge.RtId);
    }

    [Fact]
    public async Task InterfaceWithoutImplementers_ReturnsAnEmptyResult_NotAnError()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Lonely, RtEntityQueryOptions.Create(), 0, 10, true, false);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task TakeZero_ReturnsNoRows_ButTheCountOnRequest()
    {
        var repository = await SeedAsync();

        var result = await QueryAsync(repository, Named, RtEntityQueryOptions.Create(), 0, 0, true, false);

        Assert.Empty(result.Items);
        Assert.Equal(10, result.TotalCount);
    }

    /// <summary>
    ///     Ties on every sort key straddle the window end inside one collection and across collections. The server
    ///     order of tied documents is arbitrary, so the rows are inserted in reverse rtId order: without the tie
    ///     resolution the window would hold the wrong rows.
    /// </summary>
    [Fact]
    public async Task TiesAtTheWindowBoundary_AreResolvedByRtId_InsideAndAcrossCollections()
    {
        await fixture.ResetAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        foreach (var number in new[] { 25, 24, 23, 22, 21 })
        {
            await InterfaceQuerySeed.InsertAsync(repository, "Sensor", number, "same", 1);
        }

        await InterfaceQuerySeed.InsertAsync(repository, "Valve", 27, "same", 1);
        await InterfaceQuerySeed.InsertAsync(repository, "Valve", 26, "same", 1);
        await InterfaceQuerySeed.InsertAsync(repository, "Asset", 30, "same", 1);
        await InterfaceQuerySeed.InsertAsync(repository, "Asset", 20, "same", 1);
        int[] expected = [20, 21, 22, 23, 24, 25, 26, 27, 30];

        for (var skip = 0; skip < expected.Length; skip++)
        {
            for (var take = 1; take <= 4; take++)
            {
                var result = await QueryAsync(repository, Named,
                    RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), skip, take, false, true);
                Assert.Equal(expected.Skip(skip).Take(take), InterfaceQuerySeed.Numbers(result.Items));
            }
        }
    }

    /// <summary>
    ///     Sort on a multi-valued member (MongoDB sorts an array by its smallest element ascending). The tie re-query
    ///     is an equality on the array, which also matches documents that merely CONTAIN the tie value - they must not
    ///     be returned twice (review finding of PR 194).
    /// </summary>
    [Fact]
    public async Task TiesOnAMultiValuedSortMember_NeverReturnARowTwice()
    {
        await fixture.ResetAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        // Sort key (smallest element): 20 -> "0", 21..25 -> "a", 26 -> "b". Document 20 CONTAINS "a" as well.
        await InterfaceQuerySeed.InsertAsync(repository, "Sensor", 20, "x", 1, tags: ["a", "0"]);
        foreach (var number in new[] { 25, 24, 23, 22, 21 })
        {
            await InterfaceQuerySeed.InsertAsync(repository, "Sensor", number, "x", 1, tags: number == 22 ? ["z", "a"] : ["a"]);
        }

        await InterfaceQuerySeed.InsertAsync(repository, "Valve", 26, "x", 1, tags: ["b"]);
        int[] expected = [20, 21, 22, 23, 24, 25, 26];

        for (var skip = 0; skip < expected.Length; skip++)
        {
            for (var take = 1; take <= 4; take++)
            {
                var result = await QueryAsync(repository, Named,
                    RtEntityQueryOptions.Create().SortOrder("Tags", SortOrders.Ascending), skip, take, false, true);
                Assert.Equal(expected.Skip(skip).Take(take), InterfaceQuerySeed.Numbers(result.Items));
            }
        }
    }

    // ---- Security: exactly like the type query ----------------------------------------------------------------------

    private static RtDataPolicyTable VaultAndOwnedAssetsTable() => new(
    [
        // Vault: only the VaultAdmin role may read it.
        new RtDataPolicyRule("vault", new HashSet<string> { new RtCkId<CkTypeId>("InterfaceQuery/Vault").SemanticVersionedFullName },
            [RtDataAction.Read], OwnedOnly: false, AuditOnly: false, new HashSet<string> { "VaultAdmin" }),
        // Asset (and its derived Pump): everybody with the Reader role sees only what they created.
        new RtDataPolicyRule("assets", new HashSet<string> { new RtCkId<CkTypeId>("InterfaceQuery/Asset").SemanticVersionedFullName },
            [RtDataAction.Read], OwnedOnly: true, AuditOnly: false, new HashSet<string> { "Reader" })
    ]);

    private static readonly RtSecurityContext VaultAdmin = RtSecurityContext.ForUser("u-vault", ["VaultAdmin", "Reader"]);
    private static readonly RtSecurityContext Reader1 = RtSecurityContext.ForUser("u-1", ["Reader"]);
    private static readonly RtSecurityContext Reader2 = RtSecurityContext.ForUser("u-2", ["Reader"]);
    private static readonly RtSecurityContext Outsider = RtSecurityContext.ForUser("u-x", ["Nobody"]);

    private async Task<ITenantRepository> SeedForSecurityAsync()
    {
        await fixture.ResetAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        await InterfaceQuerySeed.InsertAsync(repository, "Asset", 1, "alpha", 5, Reader1);
        await InterfaceQuerySeed.InsertAsync(repository, "Asset", 2, "delta", 3, Reader2);
        await InterfaceQuerySeed.InsertAsync(repository, "Pump", 3, "bravo", 3, Reader1);
        await InterfaceQuerySeed.InsertAsync(repository, "Pump", 4, "echo", 1, Reader2);
        await InterfaceQuerySeed.InsertAsync(repository, "Sensor", 5, "charlie", 2);
        await InterfaceQuerySeed.InsertAsync(repository, "Gauge", 7, "alpha", 7);
        await InterfaceQuerySeed.InsertAsync(repository, "Valve", 8, "golf", 4);
        await InterfaceQuerySeed.InsertAsync(repository, "Vault", 10, "aaa", 0);
        fixture.Resolver.Table = VaultAndOwnedAssetsTable();
        return repository;
    }

    /// <summary>The same answer, built from the type queries of the four collection roots the caller is allowed to ask.</summary>
    private static async Task<int[]> ExpectedFromTypeQueriesAsync(ITenantRepository repository,
        RtSecurityContext? securityContext)
    {
        var all = new List<RtEntity>();
        foreach (var root in new[] { "Asset", "Sensor", "Valve", "Vault" })
        {
            using var session = securityContext == null
                ? await repository.GetSessionAsync()
                : await repository.GetSessionAsync(securityContext);
            session.StartTransaction();
            var result = await repository.GetRtEntitiesByTypeAsync(session,
                new RtCkId<CkTypeId>($"InterfaceQuery/{root}"), RtEntityQueryOptions.Create(), 0, 100);
            await session.CommitTransactionAsync();
            all.AddRange(result.Items);
        }

        return InterfaceQuerySeed.Numbers(all
            .OrderBy(InterfaceQuerySeed.NameOf, StringComparer.Ordinal).ThenBy(e => e.RtId));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("vaultAdmin")]
    [InlineData("reader1")]
    [InlineData("reader2")]
    [InlineData("outsider")]
    public async Task Security_ShowsExactlyWhatTheTypeQueriesShow(string caller)
    {
        var repository = await SeedForSecurityAsync();
        RtSecurityContext? context = caller switch
        {
            "vaultAdmin" => VaultAdmin,
            "reader1" => Reader1,
            "reader2" => Reader2,
            "outsider" => Outsider,
            _ => null
        };
        try
        {
            var expected = await ExpectedFromTypeQueriesAsync(repository, context);

            foreach (var inTransaction in new[] { true, false })
            {
                var result = await QueryAsync(repository, Named,
                    RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 50, true,
                    inTransaction, context);

                Assert.Equal(expected, InterfaceQuerySeed.Numbers(result.Items));
                Assert.Equal(expected.Length, result.TotalCount);
            }
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task Security_HandComputed_RestrictedUserNeverSeesProtectedTypesOrForeignRecords()
    {
        var repository = await SeedForSecurityAsync();
        try
        {
            // Reader1: own Asset 1 and Pump 3 (owned-only), no Vault 10; Sensor 5, Gauge 7, Valve 8 are unprotected.
            var reader1 = await QueryAsync(repository, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 50, true, false, Reader1);
            Assert.Equal(new[] { 1, 7, 3, 5, 8 }, InterfaceQuerySeed.Numbers(reader1.Items));
            Assert.Equal(5, reader1.TotalCount);

            // The VaultAdmin also sees the Vault (aaa sorts first) - and the Reader role makes the assets owned-only.
            var vaultAdmin = await QueryAsync(repository, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 50, true, true, VaultAdmin);
            Assert.Equal(10, InterfaceQuerySeed.Numbers(vaultAdmin.Items)[0]);

            // A caller without any grant: no Asset/Pump (owned-only needs the Reader role), no Vault.
            var outsider = await QueryAsync(repository, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 50, true, false, Outsider);
            Assert.Equal(new[] { 7, 5, 8 }, InterfaceQuerySeed.Numbers(outsider.Items));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task Security_ACollectionThatIsFullyDenied_IsNeverQueried()
    {
        var repository = await SeedForSecurityAsync();
        try
        {
            // Outsider: the Vault and the Asset+Pump collection are denied for good (protected, no grant): only the
            // Sensor+Gauge and Valve collections receive a query.
            using var session = await repository.GetSessionAsync(Outsider);
            using var _ = MongoRequestScope.Begin(out var stats);
            var outsider = await repository.GetRtEntitiesByInterfaceAsync(session, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 10);
            Assert.Equal(2, stats.CommandCount);
            Assert.Equal(new[] { 7, 5, 8 }, InterfaceQuerySeed.Numbers(outsider.Items));

            // Reader1: the owned-only Asset+Pump collection is queried (the filter decides per row), the Vault is not.
            using var readerSession = await repository.GetSessionAsync(Reader1);
            using var __ = MongoRequestScope.Begin(out var readerStats);
            await repository.GetRtEntitiesByInterfaceAsync(readerSession, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 10);
            Assert.Equal(3, readerStats.CommandCount);

            // The VaultAdmin has every grant: all four collections are asked.
            using var adminSession = await repository.GetSessionAsync(VaultAdmin);
            using var ___ = MongoRequestScope.Begin(out var adminStats);
            await repository.GetRtEntitiesByInterfaceAsync(adminSession, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 10);
            Assert.Equal(4, adminStats.CommandCount);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    // ---- Count: only on request, one count per collection ------------------------------------------------------------

    [Fact]
    public async Task Count_IsIssuedOnlyOnRequest_OneCommandPerCollection()
    {
        var repository = await SeedAsync();

        foreach (var inTransaction in new[] { false, true })
        {
            using var session = await repository.GetSessionAsync();
            if (inTransaction)
            {
                session.StartTransaction();
            }

            using var scope = MongoRequestScope.Begin(out var withoutCount);
            var uncounted = await repository.GetRtEntitiesByInterfaceAsync(session, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 5, includeTotalCount: false);
            var pageCommands = withoutCount.CommandCount;
            Assert.Null(uncounted.TotalCount);
            Assert.Equal(4, pageCommands); // one page query per collection, no count query

            using var scope2 = MongoRequestScope.Begin(out var withCount);
            var counted = await repository.GetRtEntitiesByInterfaceAsync(session, Named,
                RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 5, includeTotalCount: true);
            Assert.Equal(10, counted.TotalCount);
            Assert.Equal(pageCommands + 4, withCount.CommandCount); // plus one count query per collection

            if (inTransaction)
            {
                await session.CommitTransactionAsync();
            }
        }
    }

    // ---- Fail clearly: stable code and parameters ---------------------------------------------------------------------

    private static async Task<InterfaceQueryException> RefusedAsync(ITenantRepository repository,
        RtCkId<CkInterfaceId> ckInterfaceId, RtEntityQueryOptions options, int skip = 0, int take = 10)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var exception = await Assert.ThrowsAsync<InterfaceQueryException>(() =>
            repository.GetRtEntitiesByInterfaceAsync(session, ckInterfaceId, options, skip, take, true));
        Assert.Equal(InterfaceQueryException.InterfaceQueryMessageNumber, exception.MessageNumber);
        Assert.Equal(exception.Reason, exception.Parameters["reason"]);
        return exception;
    }

    [Fact]
    public async Task UnknownInterface_IsRefused()
    {
        var repository = await SeedAsync();

        var exception = await RefusedAsync(repository, new RtCkId<CkInterfaceId>("InterfaceQuery/Missing"),
            RtEntityQueryOptions.Create());

        Assert.Equal(InterfaceQueryReasons.UnknownInterface, exception.Reason);
        Assert.Equal("InterfaceQuery/Missing", exception.Parameters["ckInterfaceId"]);
    }

    [Fact]
    public async Task TextSearch_IsRefused()
    {
        var repository = await SeedAsync();

        var fullText = await RefusedAsync(repository, Named, RtEntityQueryOptions.Create().TextSearch("alpha"));
        var attribute = await RefusedAsync(repository, Named,
            RtEntityQueryOptions.Create().AttributeSearch(["Name"], "alpha"));

        Assert.Equal(InterfaceQueryReasons.TextSearchNotSupported, fullText.Reason);
        Assert.Equal(InterfaceQueryReasons.TextSearchNotSupported, attribute.Reason);
        Assert.Equal("InterfaceQuery/Named", fullText.Parameters["ckInterfaceId"]);
    }

    [Fact]
    public async Task Aggregation_IsRefused()
    {
        var repository = await SeedAsync();

        var options = RtEntityQueryOptions.Create();
        options.AggregateResult();
        var result = await RefusedAsync(repository, Named, options);

        var grouped = RtEntityQueryOptions.Create();
        grouped.AggregateFieldGroupBy("Name");
        var field = await RefusedAsync(repository, Named, grouped);

        Assert.Equal(InterfaceQueryReasons.AggregationNotSupported, result.Reason);
        Assert.Equal(InterfaceQueryReasons.AggregationNotSupported, field.Reason);
    }

    [Theory]
    [InlineData("related.InterfaceQuery-Asset->Name")]
    [InlineData("related.InterfaceQuery-Asset::exists")]
    public async Task NavigationInFilterOrSort_IsRefused(string path)
    {
        var repository = await SeedAsync();

        var filter = await RefusedAsync(repository, Named,
            RtEntityQueryOptions.Create().FieldFilter(path, FieldFilterOperator.Equals, "x"));
        var sort = await RefusedAsync(repository, Named, RtEntityQueryOptions.Create().SortOrder(path, SortOrders.Ascending));

        Assert.Equal(InterfaceQueryReasons.NavigationNotSupported, filter.Reason);
        Assert.Equal(path, filter.Parameters["attributePath"]);
        Assert.Equal(InterfaceQueryReasons.NavigationNotSupported, sort.Reason);
    }

    [Theory]
    [InlineData(1000, 1, true)]
    [InlineData(0, 1001, true)]
    [InlineData(600, 401, true)]
    [InlineData(1000, 0, false)]
    [InlineData(500, 500, false)]
    [InlineData(0, 1000, false)]
    public async Task SkipPlusTake_AboveTheCap_IsRefused(int skip, int take, bool refused)
    {
        var repository = await SeedAsync();

        if (refused)
        {
            var exception = await RefusedAsync(repository, Named, RtEntityQueryOptions.Create(), skip, take);
            Assert.Equal(InterfaceQueryReasons.PageTooLarge, exception.Reason);
            Assert.Equal(skip.ToString(), exception.Parameters["skip"]);
            Assert.Equal(take.ToString(), exception.Parameters["take"]);
            Assert.Equal("1000", exception.Parameters["maxSkipPlusTake"]);
        }
        else
        {
            var result = await QueryAsync(repository, Named, RtEntityQueryOptions.Create(), skip, take, false, true);
            Assert.NotNull(result);
        }
    }

    [Theory]
    [InlineData("Note", "filter")] // member of Pump, not of the interface
    [InlineData("Missing", "filter")]
    [InlineData("Note", "sort")]
    public async Task FilterOrSortOnANonMember_IsRefused(string member, string usage)
    {
        var repository = await SeedAsync();

        var options = usage == "filter"
            ? RtEntityQueryOptions.Create().FieldFilter(member, FieldFilterOperator.Equals, "x")
            : RtEntityQueryOptions.Create().SortOrder(member, SortOrders.Ascending);
        var exception = await RefusedAsync(repository, Named, options);

        Assert.Equal(InterfaceQueryReasons.UnknownMember, exception.Reason);
        Assert.Equal(member, exception.Parameters["attributePath"]);
        Assert.Equal(usage, exception.Parameters["usage"]);
        Assert.Equal("InterfaceQuery/Named", exception.Parameters["ckInterfaceId"]);
    }

    [Fact]
    public async Task NestedFilterOnANonMember_IsRefused()
    {
        var repository = await SeedAsync();

        var options = RtEntityQueryOptions.Create();
        options.AddNestedFilter(FieldFilterCriteria.Create(LogicalOperators.Or)
            .Field("Name", FieldFilterOperator.Equals, "alpha").Field("Note", FieldFilterOperator.Equals, "x"));

        var exception = await RefusedAsync(repository, Named, options);
        Assert.Equal(InterfaceQueryReasons.UnknownMember, exception.Reason);
    }

    [Fact]
    public async Task MoreCollectionRootsThanTheCap_IsRefused_ExactlyTheCapWorks()
    {
        await fixture.ResetAsync();
        var systemContext = fixture.GetSystemContext();
        var systemId = await fixture.GetInstalledSystemIdAsync();
        await systemContext.ImportCkModelAsync(InterfaceQueryWideModel.Build(systemId, "Wide64", 64, false));
        await systemContext.ImportCkModelAsync(InterfaceQueryWideModel.Build(systemId, "Wide65", 65, false));
        var repository = systemContext.GetTenantRepository();

        var exception = await RefusedAsync(repository, new RtCkId<CkInterfaceId>("Wide65/Named"),
            RtEntityQueryOptions.Create());
        Assert.Equal(InterfaceQueryReasons.TooManyCollectionRoots, exception.Reason);
        Assert.Equal("Wide65/Named", exception.Parameters["ckInterfaceId"]);
        Assert.Equal("65", exception.Parameters["rootCount"]);
        Assert.Equal("64", exception.Parameters["maxRoots"]);

        await InterfaceQuerySeed.InsertAsync(repository, "T64", 1, "x", 1, modelName: "Wide64");
        var result = await QueryAsync(repository, new RtCkId<CkInterfaceId>("Wide64/Named"),
            RtEntityQueryOptions.Create(), 0, 10, true, true);
        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalCount);
    }
}
