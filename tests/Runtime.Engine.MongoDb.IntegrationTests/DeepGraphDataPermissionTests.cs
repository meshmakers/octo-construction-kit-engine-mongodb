using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#6734: the deep graph (both <c>GetRtDeepGraphAsync</c> overloads) applies the caller's data
///     permissions. Graph Continent &lt;- Country &lt;- StateOrProvince, Country is the restricted type:
///     a caller without read permission gets neither the Country rows nor any association that points
///     to a Country; owned-only callers keep their own Countries; unrestricted callers and tenants
///     without policies get exactly the pre-permission result.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class DeepGraphDataPermissionTests(DataPermissionTestFixture fixture)
{
    private static readonly RtSecurityContext Management =
        RtSecurityContext.ForUser("user-m", ["TestManagement"]);

    private static readonly RtSecurityContext Employee1 =
        RtSecurityContext.ForUser("user-1", ["TestEmployee"]);

    private static readonly RtSecurityContext Employee2 =
        RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    private static readonly RtSecurityContext Outsider =
        RtSecurityContext.ForUser("user-x", ["TestOther"]);

    private static string CountryTypeId => TestCkIds.RtCkCountryTypeId.SemanticVersionedFullName;

    private static readonly RtDeepGraphFollowSpec[] ParentChildInbound =
        [new(SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound)];

    /// <summary>Country: Management reads everything, Employees only their own rows.</summary>
    private static RtDataPolicyTable CountryTable() => new(
    [
        new RtDataPolicyRule("test.countries", new HashSet<string> { CountryTypeId }, [RtDataAction.Read],
            OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" }),
        new RtDataPolicyRule("test.countries", new HashSet<string> { CountryTypeId }, [RtDataAction.Read],
            OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
    ]);

    /// <summary>Country: only Management reads, employees have no rule at all (denied).</summary>
    private static RtDataPolicyTable CountryManagementOnlyTable() => new(
    [
        new RtDataPolicyRule("test.countries", new HashSet<string> { CountryTypeId }, [RtDataAction.Read],
            OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" })
    ]);

    private sealed record Seed(
        ITenantRepository Repository,
        OctoObjectId Continent,
        OctoObjectId Country1,
        OctoObjectId Country2,
        OctoObjectId State1,
        OctoObjectId State2);

    /// <summary>
    ///     Continent with two Countries (created by Employee1 / Employee2) and one StateOrProvince
    ///     below each Country. Seeded with the policy table empty so the writes are unrestricted.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        var continent = OctoObjectId.GenerateNewId();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtContinent = await repository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.RtId = continent;
            rtContinent.Name = "Europe";
            await repository.InsertOneRtEntityAsync(session, rtContinent);
            await session.CommitTransactionAsync();
        }

        var country1 = await InsertCountryAsync(repository, Employee1, "AT", continent);
        var country2 = await InsertCountryAsync(repository, Employee2, "DE", continent);
        var state1 = await InsertStateAsync(repository, "Salzburg", country1);
        var state2 = await InsertStateAsync(repository, "Bavaria", country2);
        return new Seed(repository, continent, country1, country2, state1, state2);
    }

    private static async Task<OctoObjectId> InsertCountryAsync(ITenantRepository repository,
        RtSecurityContext securityContext, string isoCode, OctoObjectId parentContinentId)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = $"Country-{isoCode}";
        country.ISOCode = isoCode;
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(country) },
        [
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkCountryTypeId, country.RtId),
                new RtEntityId(TestCkIds.RtCkContinentTypeId, parentContinentId),
                SystemCkIds.RtCkParentChildRoleId)
        ], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
        return country.RtId;
    }

    private static async Task<OctoObjectId> InsertStateAsync(ITenantRepository repository, string name,
        OctoObjectId parentCountryId)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var state = await repository.CreateTransientRtEntityAsync<RtStateOrProvince>();
        state.RtId = OctoObjectId.GenerateNewId();
        state.Name = name;
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(state) },
        [
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, state.RtId),
                new RtEntityId(TestCkIds.RtCkCountryTypeId, parentCountryId),
                SystemCkIds.RtCkParentChildRoleId)
        ], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
        return state.RtId;
    }

    private static async Task<IResultSet<RtDeepGraphQueryResult>> DeepGraphAsync(Seed seed,
        RtSecurityContext? caller, bool directed, int? skip = null, int? take = null)
    {
        using var session = caller == null
            ? await seed.Repository.GetSessionAsync()
            : await seed.Repository.GetSessionAsync(caller);
        session.StartTransaction();
        var result = directed
            ? await seed.Repository.GetRtDeepGraphAsync(session, [seed.Continent], TestCkIds.RtCkContinentTypeId,
                RtEntityQueryOptions.Create(), ParentChildInbound, skip, take)
            : await seed.Repository.GetRtDeepGraphAsync(session, [seed.Continent], TestCkIds.RtCkContinentTypeId,
                RtEntityQueryOptions.Create(), skip, take);
        await session.CommitTransactionAsync();
        return result;
    }

    private static List<OctoObjectId> RowIds(IResultSet<RtDeepGraphQueryResult> result) =>
        result.Items.Select(i => i.Id.RtId).ToList();

    private static List<OctoObjectId> TargetIds(IResultSet<RtDeepGraphQueryResult> result) =>
        result.Items.SelectMany(i => i.Associations).Select(a => a.TargetRtId).ToList();

    /// <summary>Stable text of a result, used for the byte-identical regression.</summary>
    private static string Render(IResultSet<RtDeepGraphQueryResult> result) =>
        result.TotalCount + "|" + string.Join(";", result.Items
            .OrderBy(i => i.Id.RtId.ToString(), StringComparer.Ordinal)
            .Select(i => $"{i.Id.CkTypeId.SemanticVersionedFullName}/{i.Id.RtId}:" + string.Join(",",
                i.Associations.OrderBy(a => a.AssociationId.ToString(), StringComparer.Ordinal)
                    .Select(a =>
                        $"{a.AssociationId}>{a.AssociationRoleId?.SemanticVersionedFullName}>{a.TargetCkTypeId?.SemanticVersionedFullName}/{a.TargetRtId}"))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedType_NoRowsAndNoAssociationsToIt(bool directed)
    {
        var seed = await SeedAsync();
        fixture.Resolver.Table = CountryManagementOnlyTable();
        try
        {
            var denied = await DeepGraphAsync(seed, Employee1, directed);

            var rowIds = RowIds(denied);
            var targets = TargetIds(denied);
            Assert.Contains(seed.Continent, rowIds);
            Assert.Contains(seed.State1, rowIds); // unprotected type, readable on its own
            Assert.DoesNotContain(seed.Country1, rowIds);
            Assert.DoesNotContain(seed.Country2, rowIds);
            Assert.DoesNotContain(seed.Country1, targets);
            Assert.DoesNotContain(seed.Country2, targets);
            Assert.All(denied.Items.SelectMany(i => i.Associations),
                a => Assert.NotEqual(CountryTypeId, a.TargetCkTypeId?.SemanticVersionedFullName));
            Assert.Equal(rowIds.Count, denied.TotalCount);

            var outsider = await DeepGraphAsync(seed, Outsider, directed);
            Assert.DoesNotContain(seed.Country1, RowIds(outsider));
            Assert.DoesNotContain(seed.Country1, TargetIds(outsider));

            // A caller with the read permission gets the complete graph.
            var allowed = await DeepGraphAsync(seed, Management, directed);
            Assert.Equal(Render(await DeepGraphAsync(seed, null, directed)), Render(allowed));
            Assert.Contains(seed.Country1, RowIds(allowed));
            Assert.Contains(seed.Country2, RowIds(allowed));
            Assert.Contains(seed.Country1, TargetIds(allowed));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedOnlyPolicy_KeepsOnlyOwnRows(bool directed)
    {
        var seed = await SeedAsync();
        fixture.Resolver.Table = CountryTable();
        try
        {
            var result = await DeepGraphAsync(seed, Employee1, directed);

            var rowIds = RowIds(result);
            var targets = TargetIds(result);
            Assert.Contains(seed.Country1, rowIds); // own
            Assert.Contains(seed.Country1, targets); // State1 -> Country1
            Assert.DoesNotContain(seed.Country2, rowIds); // foreign
            Assert.DoesNotContain(seed.Country2, targets);

            var other = await DeepGraphAsync(seed, Employee2, directed);
            Assert.Contains(seed.Country2, RowIds(other));
            Assert.DoesNotContain(seed.Country1, RowIds(other));
            Assert.DoesNotContain(seed.Country1, TargetIds(other));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemContext_BypassesPolicies(bool directed)
    {
        var seed = await SeedAsync();
        var reference = Render(await DeepGraphAsync(seed, null, directed));
        fixture.Resolver.Table = CountryManagementOnlyTable();
        try
        {
            Assert.Equal(reference, Render(await DeepGraphAsync(seed, null, directed)));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    /// <summary>
    ///     Regression: a tenant without policies (and a tenant with policies that do not touch the
    ///     queried graph) yields byte-identical results to the unfiltered system session.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrestrictedTenant_IsByteIdentical(bool directed)
    {
        var seed = await SeedAsync();
        var reference = Render(await DeepGraphAsync(seed, null, directed));
        Assert.Contains(seed.Country1.ToString(), reference);

        Assert.Equal(reference, Render(await DeepGraphAsync(seed, Outsider, directed)));

        // Policy on an unrelated type (Continent is not in the Country policy): nothing is pruned.
        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.other", new HashSet<string> { TestCkIds.RtCkCustomerTypeId.SemanticVersionedFullName },
                [RtDataAction.Read], OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" })
        ]);
        try
        {
            Assert.Equal(reference, Render(await DeepGraphAsync(seed, Outsider, directed)));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paging_IsAppliedAfterPruning(bool directed)
    {
        var seed = await SeedAsync();
        fixture.Resolver.Table = CountryManagementOnlyTable();
        try
        {
            var full = await DeepGraphAsync(seed, Employee1, directed);
            Assert.Equal(3, full.TotalCount); // Continent + both States (unprotected); both Countries pruned

            var page = await DeepGraphAsync(seed, Employee1, directed, 0, 1);
            Assert.Single(page.Items);
            Assert.Equal(full.TotalCount, page.TotalCount);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }
}
