using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     RtCreatedBy stamping in the engine write path (AB#4971): user sessions stamp the caller on
///     insert (a client-supplied value never wins), system sessions round-trip a provided value
///     (import/restore), replaces preserve the stored creator, partial updates never touch it.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class RtCreatedByStampingTests(ImportTestCkModelFixture fixture)
{
    private static readonly RtSecurityContext User1 =
        RtSecurityContext.ForUser("user-1", ["AccountingEmployee"]);

    private static readonly RtSecurityContext User2 =
        RtSecurityContext.ForUser("user-2", ["AccountingEmployee"]);

    [Fact]
    public async Task Insert_UserSession_StampsCallerAndOverwritesClientSuppliedValue()
    {
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        using (var session = await tenantRepository.GetSessionAsync(User1))
        {
            session.StartTransaction();
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.RtId = OctoObjectId.GenerateNewId();
            rtContinent.Name = "Stamped";
            rtContinent.RtCreatedBy = "spoofed-user"; // must never win on a user session
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
            await session.CommitTransactionAsync();
        }

        var loaded = await LoadSingleAsync(tenantRepository);
        Assert.Equal("user-1", loaded.RtCreatedBy);
    }

    [Fact]
    public async Task Insert_SystemSession_KeepsProvidedValueAndStampsNothing()
    {
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var importedId = OctoObjectId.GenerateNewId();
        var freshId = OctoObjectId.GenerateNewId();

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();

            var imported = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            imported.RtId = importedId;
            imported.Name = "Imported";
            imported.RtCreatedBy = "original-creator"; // import/restore round-trip
            await tenantRepository.InsertOneRtEntityAsync(session, imported);

            var fresh = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            fresh.RtId = freshId;
            fresh.Name = "Fresh";
            await tenantRepository.InsertOneRtEntityAsync(session, fresh);

            await session.CommitTransactionAsync();
        }

        var loaded = await LoadAllAsync(tenantRepository);
        Assert.Equal("original-creator", loaded.Single(e => e.RtId == importedId).RtCreatedBy);
        Assert.Null(loaded.Single(e => e.RtId == freshId).RtCreatedBy);
    }

    [Fact]
    public async Task Replace_PreservesStoredCreator()
    {
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        var rtEntityId = new RtEntityId(TestCkIds.RtCkContinentTypeId, rtId);

        await InsertAsUserAsync(tenantRepository, rtId, User1);

        // Replace by another user with no RtCreatedBy on the incoming document — the stored
        // creator must survive the full-document rewrite.
        using (var session = await tenantRepository.GetSessionAsync(User2))
        {
            session.StartTransaction();
            var operationResult = new OperationResult();
            var replacement = new RtEntity(TestCkIds.RtCkContinentTypeId, rtId,
                new Dictionary<string, object?> { { "Name", "Replaced" } });
            var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
            {
                EntityUpdateInfo<RtEntity>.CreateReplace(rtEntityId, replacement)
            };
            await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
            Assert.False(operationResult.HasErrors);
            await session.CommitTransactionAsync();
        }

        var loaded = await LoadSingleAsync(tenantRepository);
        Assert.Equal("Replaced", ((RtContinent)loaded).Name);
        Assert.Equal("user-1", loaded.RtCreatedBy);
    }

    [Fact]
    public async Task Update_DoesNotTouchCreator()
    {
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        var rtEntityId = new RtEntityId(TestCkIds.RtCkContinentTypeId, rtId);

        await InsertAsUserAsync(tenantRepository, rtId, User1);

        using (var session = await tenantRepository.GetSessionAsync(User2))
        {
            session.StartTransaction();
            var operationResult = new OperationResult();
            var update = new RtEntity(TestCkIds.RtCkContinentTypeId, rtId,
                new Dictionary<string, object?> { { "Name", "Updated" } });
            var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
            {
                EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, update)
            };
            await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
            Assert.False(operationResult.HasErrors);
            await session.CommitTransactionAsync();
        }

        var loaded = await LoadSingleAsync(tenantRepository);
        Assert.Equal("Updated", ((RtContinent)loaded).Name);
        Assert.Equal("user-1", loaded.RtCreatedBy);
    }

    [Fact]
    public async Task FieldFilter_OnRtCreatedBy_Filters()
    {
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var user1Id = OctoObjectId.GenerateNewId();
        var user2Id = OctoObjectId.GenerateNewId();
        await InsertAsUserAsync(tenantRepository, user1Id, User1);
        await InsertAsUserAsync(tenantRepository, user2Id, User2);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var queryOptions = RtEntityQueryOptions.Create();
        queryOptions.AddFieldFilter("rtCreatedBy", FieldFilterOperator.Equals, "user-1");
        var loaded = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session, queryOptions);
        await session.CommitTransactionAsync();

        Assert.Single(loaded.Items);
        Assert.Equal(user1Id, loaded.Items.First().RtId);
    }

    private static async Task InsertAsUserAsync(ITenantRepository tenantRepository, OctoObjectId rtId,
        RtSecurityContext securityContext)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
        rtContinent.RtId = rtId;
        rtContinent.Name = "Initial";
        await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
        await session.CommitTransactionAsync();
    }

    private static async Task<RtEntity> LoadSingleAsync(ITenantRepository tenantRepository)
    {
        var loaded = await LoadAllAsync(tenantRepository);
        return Assert.Single(loaded);
    }

    private static async Task<IReadOnlyList<RtContinent>> LoadAllAsync(ITenantRepository tenantRepository)
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var loaded = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session,
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return loaded.Items.ToList();
    }
}
