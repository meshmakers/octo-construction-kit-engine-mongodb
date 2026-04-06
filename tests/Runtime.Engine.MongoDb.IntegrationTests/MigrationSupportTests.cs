using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for the CK-cache-free migration support methods in TenantRepository.
/// These tests verify the migration helper methods that allow manipulating RT entities
/// without requiring the source CK type to be present in the CK cache.
/// </summary>
[Collection("Sequential")]
public class MigrationSupportTests(MigrationSupportFixture fixture)
    : IClassFixture<MigrationSupportFixture>
{
    // Location types form a hierarchy: Location (abstract, collection root) -> Continent, Country, District, etc.
    // All derived types are stored in the Location collection (shared collection).
    private static readonly RtCkId<CkTypeId> ContinentTypeId = new("Test/Continent");
    private static readonly RtCkId<CkTypeId> CountryTypeId = new("Test/Country");
    private static readonly RtCkId<CkTypeId> DistrictTypeId = new("Test/District");
    private static readonly RtCkId<CkTypeId> LocationTypeId = new("Test/Location");

    // Customer is a root type with its own collection
    private static readonly RtCkId<CkTypeId> CustomerTypeId = new("Test/Customer");

    // MeasuringPoint is also a root type
    private static readonly RtCkId<CkTypeId> MeasuringPointTypeId = new("Test/MeasuringPoint");

    // Known RT IDs from sampleRtModel.yaml
    private static readonly OctoObjectId EuropeRtId = new("66803ecf4aa85720dda96a97");
    private static readonly OctoObjectId OesterreichRtId = new("66803ecf4aa85720dda96a98");

    #region GetRtEntitiesByTypeForMigrationAsync

    [Fact]
    public async Task GetRtEntitiesByTypeForMigrationAsync_RootType_ShouldReturnEntitiesAndNotShared()
    {
        // Arrange: Customer is a root type with its own collection
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var (entities, isSharedCollection) = await repository.GetRtEntitiesByTypeForMigrationAsync(
            session, CustomerTypeId);

        await session.CommitTransactionAsync();

        // Assert: 3 customers in sample data, stored in own collection (not shared)
        Assert.Equal(3, entities.Count);
        Assert.False(isSharedCollection);
    }

    [Fact]
    public async Task GetRtEntitiesByTypeForMigrationAsync_DerivedType_ShouldReturnEntitiesAndIsShared()
    {
        // Arrange: Continent is derived from Location (abstract), stored in Location's collection
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var (entities, isSharedCollection) = await repository.GetRtEntitiesByTypeForMigrationAsync(
            session, ContinentTypeId);

        await session.CommitTransactionAsync();

        // Assert: 1 continent (Europe), found in parent (Location) collection
        Assert.Single(entities);
        Assert.True(isSharedCollection);
    }

    [Fact]
    public async Task GetRtEntitiesByTypeForMigrationAsync_NonExistentType_ShouldReturnEmpty()
    {
        // Arrange: A type that doesn't exist
        var nonExistentType = new RtCkId<CkTypeId>("Test/NonExistent");
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var (entities, isSharedCollection) = await repository.GetRtEntitiesByTypeForMigrationAsync(
            session, nonExistentType);

        await session.CommitTransactionAsync();

        // Assert: No entities found, IsSharedCollection should be false (no data found)
        Assert.Empty(entities);
        Assert.False(isSharedCollection);
    }

    #endregion

    #region DeleteOneRtEntityForMigrationAsync and InsertOneRtEntityForMigrationAsync

    [Fact]
    public async Task DeleteAndInsertRtEntityForMigration_RootType_ShouldWork()
    {
        // Arrange: Insert a new customer, then delete it
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var newRtId = OctoObjectId.GenerateNewId();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Act: Insert
            var entity = new RtEntity(CustomerTypeId, newRtId);
            await repository.InsertOneRtEntityForMigrationAsync(session, CustomerTypeId, entity);

            // Verify insertion
            var (entitiesAfterInsert, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, CustomerTypeId);
            Assert.Contains(entitiesAfterInsert, e => e.RtId == newRtId);

            // Act: Delete
            await repository.DeleteOneRtEntityForMigrationAsync(session, CustomerTypeId, newRtId);

            // Verify deletion
            var (entitiesAfterDelete, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, CustomerTypeId);
            Assert.DoesNotContain(entitiesAfterDelete, e => e.RtId == newRtId);

            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    [Fact]
    public async Task DeleteOneRtEntityForMigration_DerivedTypeInSharedCollection_ShouldDeleteFromParentCollection()
    {
        // Arrange: The migration flow for derived types stored in a parent collection is:
        // 1. Get entities from parent collection (GetRtEntitiesByTypeForMigrationAsync)
        // 2. Delete from parent collection (DeleteOneRtEntityForMigrationAsync)
        // 3. Insert into new per-type collection (InsertOneRtEntityForMigrationAsync)
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Discover continent entities (sets up _derivedTypeCollectionMap pointing to Location collection)
            var (continentsBefore, isShared) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, ContinentTypeId);
            Assert.True(isShared);
            Assert.Single(continentsBefore); // Europe

            // Act: Delete the continent entity from the shared Location collection
            var europeId = continentsBefore[0].RtId;
            await repository.DeleteOneRtEntityForMigrationAsync(session, ContinentTypeId, europeId);

            // Assert: Continent should no longer be found in the shared collection
            var (continentsAfter, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, ContinentTypeId);
            Assert.Empty(continentsAfter);

            // Re-insert to restore data (into its own Continent collection — this is the migration pattern)
            var entity = new RtEntity(ContinentTypeId, europeId);
            await repository.InsertOneRtEntityForMigrationAsync(session, ContinentTypeId, entity);

            // Abort to avoid persisting changes to sample data
            await session.AbortTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    #endregion

    #region UpdateCkTypeIdForMigrationAsync

    [Fact]
    public async Task UpdateCkTypeIdForMigrationAsync_EntityInSharedCollection_ShouldUpdateCkTypeId()
    {
        // Arrange: Use an existing entity in the shared Location collection
        // and update its CkTypeId, then revert
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Discover continent entities (populates _derivedTypeCollectionMap → Location collection)
            var (continents, isShared) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, ContinentTypeId);
            Assert.True(isShared);
            Assert.Single(continents);
            var europeId = continents[0].RtId;

            // Act: Update the CkTypeId from Continent to Country (both in same Location collection)
            await repository.UpdateCkTypeIdForMigrationAsync(session, europeId, CountryTypeId);

            // Assert: The entity should now appear as a Country
            var (countries, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(session, CountryTypeId);
            Assert.Contains(countries, e => e.RtId == europeId);

            // And it should no longer appear as a Continent
            var (continentsAfter, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, ContinentTypeId);
            Assert.DoesNotContain(continentsAfter, e => e.RtId == europeId);

            // Abort to avoid persisting changes to sample data
            await session.AbortTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    [Fact]
    public async Task UpdateCkTypeIdForMigrationAsync_FindsCorrectCollection_WhenMultipleCollectionsMapped()
    {
        // This tests the fix for the Copilot review comment:
        // UpdateCkTypeIdForMigrationAsync should search through collections to find the entity,
        // not just pick the first entry in _derivedTypeCollectionMap.
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Discover multiple derived types to populate the collection map with multiple entries
            // Both Continent and Country map to the same Location collection
            var (continents, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, ContinentTypeId);
            await repository.GetRtEntitiesByTypeForMigrationAsync(session, CountryTypeId);

            Assert.Single(continents);
            var europeId = continents[0].RtId;

            // Act: Update CkTypeId of Europe from Continent to District
            // The method should correctly find the entity in the Location collection
            // despite multiple entries in the collection map
            await repository.UpdateCkTypeIdForMigrationAsync(session, europeId, DistrictTypeId);

            // Assert: Entity should be findable as District now
            var (districts, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                session, DistrictTypeId);
            Assert.Contains(districts, e => e.RtId == europeId);

            // Abort to avoid persisting changes
            await session.AbortTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    #endregion

    #region UpdateAssociationCkTypeIdsForMigrationAsync

    [Fact]
    public async Task UpdateAssociationCkTypeIdsForMigrationAsync_ShouldUpdateAndReturnCorrectCount()
    {
        // Arrange: Insert test associations with known CkTypeIds, update them, verify count
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var oldTypeId = new RtCkId<CkTypeId>("Test/District");
        var newTypeId = new RtCkId<CkTypeId>("Test/DistrictRenamed");

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Act: Update all associations referencing District to DistrictRenamed
            // The sample data has associations with targetCkTypeId = Test/District
            var updatedCount = await repository.UpdateAssociationCkTypeIdsForMigrationAsync(
                session, oldTypeId, newTypeId);

            // Assert: Count should reflect actual matches (before update)
            // District associations exist in sample data (municipalities point to districts)
            Assert.True(updatedCount > 0, "Should have updated at least one association");

            // Revert: Update back to original
            var revertedCount = await repository.UpdateAssociationCkTypeIdsForMigrationAsync(
                session, newTypeId, oldTypeId);
            Assert.Equal(updatedCount, revertedCount);

            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    [Fact]
    public async Task UpdateAssociationCkTypeIdsForMigrationAsync_NoMatchingAssociations_ShouldReturnZero()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var nonExistentType = new RtCkId<CkTypeId>("Test/NonExistentType");
        var newTypeId = new RtCkId<CkTypeId>("Test/SomethingElse");

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        // Act
        var updatedCount = await repository.UpdateAssociationCkTypeIdsForMigrationAsync(
            session, nonExistentType, newTypeId);

        await session.CommitTransactionAsync();

        // Assert
        Assert.Equal(0, updatedCount);
    }

    #endregion

    #region DropCollectionIfEmptyForMigrationAsync

    [Fact]
    public async Task DropCollectionIfEmptyForMigrationAsync_NonEmptyCollection_ShouldReturnFalse()
    {
        // Arrange: Customer collection has data
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();

        // Act
        var dropped = await repository.DropCollectionIfEmptyForMigrationAsync(CustomerTypeId);

        // Assert: Collection has data, should not be dropped
        Assert.False(dropped);

        // Verify data is still there
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var (entities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(session, CustomerTypeId);
        await session.CommitTransactionAsync();

        Assert.True(entities.Count > 0, "Customer entities should still exist after failed drop");
    }

    [Fact]
    public async Task DropCollectionIfEmptyForMigrationAsync_EmptyCollection_ShouldDropAndReturnTrue()
    {
        // Arrange: Create a temporary type's collection, ensure it's empty, then drop it
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var tempTypeId = new RtCkId<CkTypeId>("Test/TempMigrationDropTest");

        // Insert and immediately delete to ensure the collection exists but is empty
        var tempRtId = OctoObjectId.GenerateNewId();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var entity = new RtEntity(tempTypeId, tempRtId);
            await repository.InsertOneRtEntityForMigrationAsync(session, tempTypeId, entity);
            await repository.DeleteOneRtEntityForMigrationAsync(session, tempTypeId, tempRtId);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }

        // Act: Drop the now-empty collection
        var dropped = await repository.DropCollectionIfEmptyForMigrationAsync(tempTypeId);

        // Assert
        Assert.True(dropped);
    }

    #endregion

    #region InsertOneRtEntityForMigrationAsync

    [Fact]
    public async Task InsertOneRtEntityForMigrationAsync_ShouldSetTimestamps()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var newRtId = OctoObjectId.GenerateNewId();
        var beforeInsert = DateTime.UtcNow;

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Act: Insert a new entity without timestamps set
            var entity = new RtEntity(CustomerTypeId, newRtId);
            await repository.InsertOneRtEntityForMigrationAsync(session, CustomerTypeId, entity);

            // Fetch it back
            var (entities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(session, CustomerTypeId);
            var inserted = entities.FirstOrDefault(e => e.RtId == newRtId);

            // Assert: Timestamps should be set
            Assert.NotNull(inserted);
            Assert.NotNull(inserted.RtCreationDateTime);
            Assert.NotNull(inserted.RtChangedDateTime);
            Assert.True(inserted.RtCreationDateTime >= beforeInsert);
            Assert.True(inserted.RtChangedDateTime >= beforeInsert);

            // Cleanup
            await repository.DeleteOneRtEntityForMigrationAsync(session, CustomerTypeId, newRtId);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    #endregion
}
