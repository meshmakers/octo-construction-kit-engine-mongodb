using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Conditional-update path through <see cref="EntityUpdateInfo{TEntity}.CreateConditionalUpdate" />:
///     verifies the MongoDB backend honours <see cref="AttributeNewerThanGuard" /> as a
///     filter on the <c>UpdateOne</c> call so a stale write does not overwrite a more-recent
///     persisted value.
/// </summary>
[Collection("Sequential")]
public class ConditionalUpdateTests(ImportTestCkModelFixture fixture) : IClassFixture<ImportTestCkModelFixture>
{
    [Fact]
    public async Task ConditionalUpdate_GuardOlderThanPersisted_NoOp()
    {
        // Setup: insert a Continent; the engine writes rtChangedDateTime = DateTime.UtcNow.
        // The guard then asks "apply only if persisted rtChangedDateTime <= guard.NewValue"
        // with a NewValue 1 hour BEFORE the insert. The persisted value is later, so the
        // filter must reject the update and Name must stay at the initial value.
        await fixture.ClearCollectionAsync();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        var rtEntityId = new RtEntityId(TestCkIds.RtCkContinentTypeId, rtId);

        // Insert.
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.RtId = rtId;
            rtContinent.Name = "Initial";
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
            await session.CommitTransactionAsync();
        }

        var insertTimestampUtc = DateTime.UtcNow;
        var guardOlder = new AttributeNewerThanGuard("rtChangedDateTime",
            insertTimestampUtc.AddHours(-1));

        // Conditional update with a guard timestamp older than the persisted rtChangedDateTime.
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var operationResult = new OperationResult();
            var updateRtEntity = new RtEntity(TestCkIds.RtCkContinentTypeId, rtId,
                new Dictionary<string, object?> { { "Name", "Should-Not-Apply" } });
            var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
            {
                EntityUpdateInfo<RtEntity>.CreateConditionalUpdate(rtEntityId, updateRtEntity, guardOlder)
            };
            await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
            Assert.False(operationResult.HasErrors);
            Assert.False(operationResult.HasFatalErrors);
            await session.CommitTransactionAsync();
        }

        // Assert: Name still "Initial" — the guard rejected the stale write.
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var loaded = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session,
                RtEntityQueryOptions.Create());
            await session.CommitTransactionAsync();

            Assert.Single(loaded.Items);
            Assert.Equal("Initial", loaded.Items.First().Name);
        }
    }

    [Fact]
    public async Task ConditionalUpdate_GuardNewerThanPersisted_AppliesWrite()
    {
        // Setup: insert Continent; persisted rtChangedDateTime is ~now. Use a guard with a
        // NewValue WELL IN THE FUTURE — the persisted value is then "<= guard.NewValue", so
        // the filter matches and the update applies normally.
        await fixture.ClearCollectionAsync();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        var rtEntityId = new RtEntityId(TestCkIds.RtCkContinentTypeId, rtId);

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.RtId = rtId;
            rtContinent.Name = "Initial";
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
            await session.CommitTransactionAsync();
        }

        var guardNewer = new AttributeNewerThanGuard("rtChangedDateTime",
            DateTime.UtcNow.AddHours(1));

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var operationResult = new OperationResult();
            var updateRtEntity = new RtEntity(TestCkIds.RtCkContinentTypeId, rtId,
                new Dictionary<string, object?> { { "Name", "Updated" } });
            var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
            {
                EntityUpdateInfo<RtEntity>.CreateConditionalUpdate(rtEntityId, updateRtEntity, guardNewer)
            };
            await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
            Assert.False(operationResult.HasErrors);
            Assert.False(operationResult.HasFatalErrors);
            await session.CommitTransactionAsync();
        }

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var loaded = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session,
                RtEntityQueryOptions.Create());
            await session.CommitTransactionAsync();

            Assert.Single(loaded.Items);
            Assert.Equal("Updated", loaded.Items.First().Name);
        }
    }
}
