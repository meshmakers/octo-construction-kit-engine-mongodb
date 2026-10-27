using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>Seeding helpers of the interface query tests (AB#7019).</summary>
internal static class InterfaceQuerySeed
{
    /// <summary>A predictable rtId: ids order by their number, which is the tie-break of the interface query.</summary>
    internal static OctoObjectId Id(int number) => OctoObjectId.Parse(number.ToString("x24"));

    internal static async Task InsertAsync(ITenantRepository repository, string type, int number, string name,
        int? rank, RtSecurityContext? securityContext = null, string modelName = "InterfaceQuery")
    {
        var entity = await repository.CreateTransientRtEntityAsync(
            new CkId<CkTypeId>(new CkModelId($"{modelName}-1.0.0"), new CkTypeId($"{type}-1")));
        entity.RtId = Id(number);
        entity.SetAttributeValue("Name", AttributeValueTypesDto.String, name);
        if (rank.HasValue)
        {
            entity.SetAttributeValue("Rank", AttributeValueTypesDto.Integer, rank.Value);
        }

        using var session = securityContext == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, new RtCkId<CkTypeId>($"{modelName}/{type}"), entity);
        await session.CommitTransactionAsync();
    }

    /// <summary>
    ///     The standard data set (hand-computed expectations in the tests refer to these numbers):
    ///     Asset+Pump share a collection, Sensor+Gauge share one, Valve and Vault have one each; 11 is archived.
    /// </summary>
    internal static async Task SeedStandardAsync(ITenantRepository repository)
    {
        await InsertAsync(repository, "Asset", 1, "alpha", 5);
        await InsertAsync(repository, "Asset", 2, "delta", 3);
        await InsertAsync(repository, "Pump", 3, "bravo", 3);
        await InsertAsync(repository, "Pump", 4, "echo", 1);
        await InsertAsync(repository, "Sensor", 5, "charlie", 2);
        await InsertAsync(repository, "Sensor", 6, "foxtrot", 9);
        await InsertAsync(repository, "Gauge", 7, "alpha", 7);
        await InsertAsync(repository, "Valve", 8, "golf", 4);
        await InsertAsync(repository, "Valve", 9, "bravo", 8);
        await InsertAsync(repository, "Vault", 10, "aaa", 0);
        await InsertAsync(repository, "Asset", 11, "zulu", 1);

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.DeleteOneRtEntityAsync(session, new RtCkId<CkTypeId>("InterfaceQuery/Asset"),
            Meshmakers.Octo.Runtime.Contracts.Repositories.Query.FieldFilterCriteria.Create()
                .Field("RtId", Meshmakers.Octo.Runtime.Contracts.Repositories.Query.FieldFilterOperator.Equals, Id(11)),
            DeleteOptions.Default);
        await session.CommitTransactionAsync();
    }

    internal static string NameOf(RtEntity entity) =>
        (string)entity.Attributes.First(a => string.Equals(a.Key, "Name", StringComparison.OrdinalIgnoreCase)).Value!;

    internal static int[] Numbers(IEnumerable<RtEntity> entities) =>
        entities.Select(e => Convert.ToInt32(e.RtId.ToString()[^8..], 16)).ToArray();
}
