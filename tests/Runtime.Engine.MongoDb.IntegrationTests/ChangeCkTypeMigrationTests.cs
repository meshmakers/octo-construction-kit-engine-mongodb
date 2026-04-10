using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for ChangeCkType migration operations.
/// Verifies that entity ckTypeId updates and association reference updates
/// work correctly when split across separate transactions (entity updates
/// committed first, association updates run outside the transaction).
/// </summary>
[Collection("Sequential")]
public class ChangeCkTypeMigrationTests(MigrationSupportFixture fixture)
    : IClassFixture<MigrationSupportFixture>
{
    private static readonly RtCkId<CkTypeId> DistrictTypeId = new("Test/District");
    private static readonly RtCkId<CkTypeId> RenamedDistrictTypeId = new("Test/DistrictMigrated");

    [Fact]
    public async Task ChangeCkType_SplitTransaction_ShouldUpdateEntitiesAndAssociationsSeparately()
    {
        // This test mirrors the fixed ExecuteTransformAsync pattern:
        // 1. Entity ckTypeId updates inside a transaction
        // 2. Association reference updates outside the transaction (separate session)
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();

        // --- Phase 1: Update entity ckTypeIds in a transaction ---
        var entitySession = await repository.GetSessionAsync();
        entitySession.StartTransaction();

        IReadOnlyList<RtEntity> entities;
        try
        {
            (entities, var isShared) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                entitySession, DistrictTypeId);

            Assert.NotEmpty(entities);
            Assert.True(isShared, "District is a derived type in the Location shared collection");

            foreach (var entity in entities)
            {
                await repository.UpdateCkTypeIdForMigrationAsync(
                    entitySession, entity.RtId, RenamedDistrictTypeId);
            }

            await entitySession.CommitTransactionAsync();
        }
        catch
        {
            await entitySession.AbortTransactionAsync();
            throw;
        }

        // --- Phase 2: Update association references outside any transaction ---
        var assocSession = await repository.GetSessionAsync();
        var updatedAssocCount = await repository.UpdateAssociationCkTypeIdsForMigrationAsync(
            assocSession, DistrictTypeId, RenamedDistrictTypeId);

        Assert.True(updatedAssocCount > 0,
            "Should have updated association references pointing to District");

        // --- Verify: Entities are now under the new type ---
        var verifySession = await repository.GetSessionAsync();
        verifySession.StartTransaction();

        var (migratedEntities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
            verifySession, RenamedDistrictTypeId);
        var (oldEntities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
            verifySession, DistrictTypeId);

        await verifySession.CommitTransactionAsync();

        Assert.Equal(entities.Count, migratedEntities.Count);
        Assert.Empty(oldEntities);

        // --- Cleanup: Revert to original state ---
        var revertSession = await repository.GetSessionAsync();
        revertSession.StartTransaction();
        try
        {
            foreach (var entity in migratedEntities)
            {
                await repository.UpdateCkTypeIdForMigrationAsync(
                    revertSession, entity.RtId, DistrictTypeId);
            }

            await revertSession.CommitTransactionAsync();
        }
        catch
        {
            await revertSession.AbortTransactionAsync();
            throw;
        }

        var revertAssocSession = await repository.GetSessionAsync();
        await repository.UpdateAssociationCkTypeIdsForMigrationAsync(
            revertAssocSession, RenamedDistrictTypeId, DistrictTypeId);
    }

    [Fact]
    public async Task ChangeCkType_AssociationUpdateFailure_ShouldNotRollBackEntityUpdates()
    {
        // Verifies that if association updates fail after entity commit,
        // the entity changes are preserved (not rolled back).
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();

        // Phase 1: Update entity ckTypeIds
        var entitySession = await repository.GetSessionAsync();
        entitySession.StartTransaction();

        IReadOnlyList<RtEntity> entities;
        try
        {
            (entities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                entitySession, DistrictTypeId);

            Assert.NotEmpty(entities);

            foreach (var entity in entities)
            {
                await repository.UpdateCkTypeIdForMigrationAsync(
                    entitySession, entity.RtId, RenamedDistrictTypeId);
            }

            await entitySession.CommitTransactionAsync();
        }
        catch
        {
            await entitySession.AbortTransactionAsync();
            throw;
        }

        // Phase 2: Simulate association update (don't actually update — pretend it failed)
        // The point: entity updates should already be persisted regardless.

        // Verify: Entities are committed even without association updates
        var verifySession = await repository.GetSessionAsync();
        verifySession.StartTransaction();

        var (migratedEntities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
            verifySession, RenamedDistrictTypeId);

        await verifySession.CommitTransactionAsync();

        Assert.Equal(entities.Count, migratedEntities.Count);

        // Cleanup: Revert entities and associations
        var revertSession = await repository.GetSessionAsync();
        revertSession.StartTransaction();
        try
        {
            foreach (var entity in migratedEntities)
            {
                await repository.UpdateCkTypeIdForMigrationAsync(
                    revertSession, entity.RtId, DistrictTypeId);
            }

            await revertSession.CommitTransactionAsync();
        }
        catch
        {
            await revertSession.AbortTransactionAsync();
            throw;
        }
    }
}
