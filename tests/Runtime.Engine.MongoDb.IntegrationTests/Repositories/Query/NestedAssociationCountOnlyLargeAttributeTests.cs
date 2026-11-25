using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     The count-only form of a nested association on a CK type with large attributes: twenty countries of about 1 MB each below one continent. Together
///     the targets exceed MongoDB's 16 MB document limit, so every pipeline that collects the target documents of the
///     origin into one array fails - as the unpaged nested query does. The count-only form (take 0) counts them
///     without loading a target document; a paged query still works and reports the same total.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class NestedAssociationCountOnlyLargeAttributeTests(ImportTestCkModelFixture fixture)
{
    private const int CountryCount = 20;
    private const int NameBytes = 1024 * 1024;

    [Fact]
    public async Task CountOnly_OfTargetsLargerThanADocument_CountsWithoutLoadingThem()
    {
        var (repository, continentRtId) = await SeedAsync();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var countOnly = (await CountriesOfAsync(repository, session, continentRtId, null, 0)).Single().Value;
        var paged = (await CountriesOfAsync(repository, session, continentRtId, 0, 2)).Single().Value;

        await session.CommitTransactionAsync();

        Assert.Equal(CountryCount, countOnly.TotalCount);
        Assert.Empty(countOnly.Items);
        Assert.Equal(CountryCount, paged.TotalCount);
        Assert.Equal(2, paged.Items.Count());
    }

    [Fact]
    public async Task UnpagedQuery_OfTargetsLargerThanADocument_Fails()
    {
        // The hazard the count-only form avoids: before, a nested totalCount without first ran exactly this
        // query. Should the unpaged query ever stop collecting the targets into one array, this test documents it
        // and may go.
        var (repository, continentRtId) = await SeedAsync();

        using var session = await repository.GetSessionAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => CountriesOfAsync(repository, session, continentRtId, null, null));
    }

    private static Task<IMultipleOriginResultSet<RtEntity>> CountriesOfAsync(ITenantRepository repository,
        IOctoSession session, OctoObjectId continentRtId, int? skip, int? take)
    {
        return repository.GetRtAssociationTargetsAsync(session, [continentRtId], TestCkIds.RtCkContinentTypeId,
            SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkCountryTypeId, GraphDirections.Inbound, null,
            RtEntityQueryOptions.Create(), skip, take);
    }

    private async Task<(ITenantRepository Repository, OctoObjectId ContinentRtId)> SeedAsync()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = "Large-continent";
        await ApplyAsync(repository, continent, null);

        for (var i = 0; i < CountryCount; i++)
        {
            // One country per transaction keeps every change set far below the limits of a single oplog entry; the
            // country and its mandatory parent edge go into one change set (the multiplicity is checked per set).
            var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
            country.RtId = OctoObjectId.GenerateNewId();
            country.Name = $"{i:D2}-" + new string((char)('a' + i % 26), NameBytes);
            country.ISOCode = $"L{i:D2}";
            await ApplyAsync(repository, country, AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkCountryTypeId, country.RtId),
                new RtEntityId(TestCkIds.RtCkContinentTypeId, continent.RtId),
                SystemCkIds.RtCkParentChildRoleId));
        }

        return (repository, continent.RtId);
    }

    private static async Task ApplyAsync(ITenantRepository repository, RtEntity entity,
        AssociationUpdateInfo? parentEdge)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(entity) },
            parentEdge == null ? [] : [parentEdge], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
    }
}
