using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using MongoDB.Bson;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     The count-only form of a nested association (take 0) counts only the targets the caller may see, like the
///     top-level count. Three countries below one continent, two created by Employee1 and one by
///     Employee2; the policy lets a TestEmployee read only the countries they created. The caller's data-permission
///     filter reaches the count-only pipeline as a target filter, so a sparse origin is counted by that pipeline and a
///     dense origin never takes the edge-count shortcut (which would count the hidden country). Runs in
///     <see cref="ReverseNavigationThresholdCollection" />, because the dense case lowers the static threshold.
/// </summary>
[Collection(ReverseNavigationThresholdCollection.Name)]
public class NestedAssociationCountOnlyDataPermissionTests(DataPermissionTestFixture fixture)
{
    private static readonly RtSecurityContext Employee1 =
        RtSecurityContext.ForUser("user-1", ["TestEmployee"]);

    private static readonly RtSecurityContext Employee2 =
        RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountOnly_OfARestrictedCaller_CountsOnlyTheVisibleTargets(bool denseOrigin)
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        var originalThreshold = ReverseNavigationOptions.EdgeThreshold;
        try
        {
            var (repository, continentRtId) = await SeedAsync();
            fixture.Resolver.Table = new RtDataPolicyTable(
            [
                new RtDataPolicyRule("test.countries",
                    new HashSet<string> { TestCkIds.RtCkCountryTypeId.SemanticVersionedFullName },
                    [RtDataAction.Read], OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
            ]);

            // Dense: every origin with an edge is above the threshold, so without a target filter the edge count
            // (3) would answer it without a probe.
            if (denseOrigin)
            {
                ReverseNavigationOptions.EdgeThreshold = 0;
            }

            var (employeeCount, employeeObserved) = await CountOnlyAsync(repository, continentRtId, Employee1);
            var employeeItems = await ItemsAsync(repository, continentRtId, Employee1);
            var (systemCount, _) = await CountOnlyAsync(repository, continentRtId, null);

            Assert.Equal(2, employeeItems.TotalCount);
            Assert.Equal(2, employeeItems.Items.Count());
            Assert.Equal(2, employeeCount.TotalCount);
            Assert.Empty(employeeCount.Items);
            // The count-only pipeline ran for the restricted caller, with the data-permission filter in the probe.
            NestedAssociationCountOnlyTests.AssertCountOnlyAssociationStages(Assert.Single(employeeObserved));
            Assert.Contains(employeeObserved[0], s => s.Contains("$lookup") &&
                s["$lookup"]["pipeline"].AsBsonArray.Count(stage => stage.AsBsonDocument.Contains("$match")) >= 2);
            Assert.Equal(3, systemCount.TotalCount);
        }
        finally
        {
            ReverseNavigationOptions.EdgeThreshold = originalThreshold;
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private static async Task<(IResultSet<RtEntity> Result, List<IReadOnlyList<BsonDocument>> Observed)>
        CountOnlyAsync(ITenantRepository repository, OctoObjectId continentRtId, RtSecurityContext? securityContext)
    {
        var tenantRepository = (TenantRepository)repository;
        using var session = securityContext == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(securityContext);
        session.StartTransaction();

        var observed = new List<IReadOnlyList<BsonDocument>>();
        var query = await tenantRepository.CreateRtAssociationTargetsQueryAsync<RtEntity>(session, [continentRtId],
            TestCkIds.RtCkContinentTypeId, SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, null,
            [TestCkIds.RtCkCountryTypeId], RtEntityQueryOptions.Create());
        query.CountOnlyPipelineObserver = observed.Add;
        var result = Assert.Single(await query.ExecuteQuery(session, null, 0)).Value;

        await session.CommitTransactionAsync();
        return (result, observed);
    }

    private static async Task<IResultSet<RtEntity>> ItemsAsync(ITenantRepository repository,
        OctoObjectId continentRtId, RtSecurityContext securityContext)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var result = await repository.GetRtAssociationTargetsAsync(session, [continentRtId],
            TestCkIds.RtCkContinentTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkCountryTypeId,
            GraphDirections.Inbound, null, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result.Single().Value;
    }

    private async Task<(ITenantRepository Repository, OctoObjectId ContinentRtId)> SeedAsync()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        OctoObjectId continentRtId;
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
            continent.RtId = OctoObjectId.GenerateNewId();
            continent.Name = "Permission-Continent";
            await repository.InsertOneRtEntityAsync(session, continent);
            await session.CommitTransactionAsync();
            continentRtId = continent.RtId;
        }

        await InsertCountryAsync(repository, Employee1, "E1", continentRtId);
        await InsertCountryAsync(repository, Employee1, "E2", continentRtId);
        await InsertCountryAsync(repository, Employee2, "E3", continentRtId);
        return (repository, continentRtId);
    }

    private static async Task InsertCountryAsync(ITenantRepository repository, RtSecurityContext securityContext,
        string isoCode, OctoObjectId parentContinentId)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = $"Country-{isoCode}";
        country.ISOCode = isoCode;
        // The country and its mandatory parent edge go into one change set (multiplicities are checked per set).
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
    }
}
