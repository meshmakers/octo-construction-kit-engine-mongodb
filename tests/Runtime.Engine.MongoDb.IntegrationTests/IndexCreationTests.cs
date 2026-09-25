using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(ImportTestCkModelCollection.Name)]
public class IndexCreationTests
{
    private readonly ImportTestCkModelFixture _fixture;

    public IndexCreationTests(ImportTestCkModelFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }


    private static class Constants
    {
        public const string SimpleTypeName = "SimpleType";
        public const string SimpleModelName = "SimpleModel";
        public const string SimpleFieldName = "SimpleField";
        public const string DuplicateValue = "DuplicateValue";
        public const string UniqueValue = "UniqueValue";
        public const string AbstractRootModelName = "AbstractRootModel";
        public const string AbstractRootTypeName = "AbstractRoot";
        public const string ConcreteChildTypeName = "ConcreteChild";
        public const string ConcreteChildModelName = "ConcreteChildModel";
    }

    private static CkId<CkTypeId> GetSimpleTypeId(string modelName) => new($"{modelName}/{Constants.SimpleTypeName}");

    private static string GetModelVersion(string modelName, string version = "1.0.0") => $"{modelName}-{version}";
    [Fact]
    public async Task SuccessfulIndexCreation_ShouldBeTrackedInCkType()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var tenantRepository = tenantContext.GetTenantRepository();

            // Act - No need to create indexes manually, they will be created automatically on

            // Get a CkType that should have indexes (System-1.0.1/Entity is a good candidate)
            var session = tenantRepository.GetSession();
            var result = await tenantRepository.GetCkTypeAsync(
                session,
                new List<CkId<CkTypeId>> { SystemCkIds.CkEntityTypeId },
                RtEntityQueryOptions.Create());

            // Assert
            var ckType = result.Items.SingleOrDefault();
            Assert.NotNull(ckType);
            Assert.NotNull(ckType.IndexStates);
            Assert.NotEmpty(ckType.IndexStates);

            // Check that at least one index was successfully applied
            var appliedIndex = ckType.IndexStates.FirstOrDefault(s => s.State == IndexState.Applied);
            Assert.NotNull(appliedIndex);
            Assert.NotNull(appliedIndex.Name);
            Assert.NotNull(appliedIndex.CollectionName);
            Assert.NotNull(appliedIndex.AppliedAt);
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueIndexViolation_ShouldBeTrackedInCkType()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var tenantRepository = tenantContext.GetTenantRepository();

            var modelName = Constants.SimpleModelName;

            // Step 1: Create a simple CK model with 1 type and 1 attribute (no index yet)
            var modelWithoutIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), hasUniqueIndex: false);
            await tenantContext.ImportCkModelAsync(modelWithoutIndex);

            // Step 2: Add Sample Data That Violates the Unique index constraints
            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            // Insert two entities with duplicate SimpleField values
            var typeId = GetSimpleTypeId(modelName);
            var entity1 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity1.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity1);

            var entity2 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue); // Same value as entity1
            await tenantRepository.InsertOneRtEntityAsync(session, entity2);

            await session.CommitTransactionAsync();

            // Step 3: Modify the ConstructionKit to add the violating unique index
            // Import a new version of the model with a unique index - this will update the existing model
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName, "2.0.0"), hasUniqueIndex: true);

            // Act - Apply this new ConstructionKit
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert - Verify a) It didn't fail b) the unique indices that failed are available via CkType
            var session2 = tenantRepository.GetSession();
            var ckTypeId = GetSimpleTypeId(GetModelVersion(modelName, "2.0.0"));
            var result = await tenantRepository.GetCkTypeAsync(
                session2,
                new List<CkId<CkTypeId>> { ckTypeId },
                RtEntityQueryOptions.Create());

            await tenantRepository.GetRtEntitiesByTypeAsync(session2, typeId.ToRtCkId(), RtEntityQueryOptions.Create());

            var ckType = result.Items.FirstOrDefault();
            Assert.NotNull(ckType);
            Assert.NotNull(ckType.IndexStates);
            Assert.NotEmpty(ckType.IndexStates);

            // Check that the unique index failed to be created
            var failedIndex = ckType.IndexStates.FirstOrDefault(s => s.State == IndexState.Failed);
            Assert.NotNull(failedIndex);
            Assert.NotNull(failedIndex.Name);
            Assert.NotNull(failedIndex.CollectionName);
            Assert.NotNull(failedIndex.ErrorMessage);
            Assert.Contains("duplicate", failedIndex.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
           //  Cleanup - Delete child tenant
           using var cleanupSession = await systemContext.GetAdminSessionAsync();
           cleanupSession.StartTransaction();
           await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
           await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueIndexViolation_AfterDataFixed_ShouldSucceed()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var tenantRepository = tenantContext.GetTenantRepository();
            var modelName = Constants.SimpleModelName;

            // Step 1: Create model and add duplicate data
            var modelWithoutIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), hasUniqueIndex: false);
            await tenantContext.ImportCkModelAsync(modelWithoutIndex);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var typeId = GetSimpleTypeId(modelName);
            var entity1 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity1.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity1);

            var entity2 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity2);

            await session.CommitTransactionAsync();

            // Step 2: Import with unique index - should fail
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName, "2.0.0"), hasUniqueIndex: true);
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Verify index failed
            var session2 = tenantRepository.GetSession();
            var ckTypeIdV2 = GetSimpleTypeId(GetModelVersion(modelName, "2.0.0"));
            var result = await tenantRepository.GetCkTypeAsync(session2, new List<CkId<CkTypeId>> { ckTypeIdV2 }, RtEntityQueryOptions.Create());
            var ckType = result.Items.FirstOrDefault();
            Assert.NotNull(ckType);
            var failedIndex = ckType.IndexStates?.FirstOrDefault(s => s.State == IndexState.Failed);
            Assert.NotNull(failedIndex);

            // Step 3: Fix the data - update entity2 to have different value
            using var session3 = await tenantRepository.GetSessionAsync();
            session3.StartTransaction();

            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.UniqueValue);
            await tenantRepository.UpdateOneRtEntityByIdAsync(session3, ckTypeIdV2.ToRtCkId(), entity2.RtId, entity2);

            await session3.CommitTransactionAsync();

            // Act: Re-import a new version of the model with unique index (after data fix)
            var modelWithIndexRetry = CreateSimpleModel(modelName, GetModelVersion(modelName, "3.0.0"), hasUniqueIndex: true);
            await tenantContext.ImportCkModelAsync(modelWithIndexRetry);

            // Assert: Index should now be successfully applied
            var session4 = tenantRepository.GetSession();
            var ckTypeIdV3 = GetSimpleTypeId(GetModelVersion(modelName, "3.0.0"));
            var result2 = await tenantRepository.GetCkTypeAsync(session4, new List<CkId<CkTypeId>> { ckTypeIdV3 }, RtEntityQueryOptions.Create());
            var ckType2 = result2.Items.FirstOrDefault();
            Assert.NotNull(ckType2);
            Assert.NotNull(ckType2.IndexStates);
            Assert.NotEmpty(ckType2.IndexStates);

            var appliedIndex = ckType2.IndexStates.FirstOrDefault(s => s.State == IndexState.Applied);
            Assert.NotNull(appliedIndex);
            Assert.NotNull(appliedIndex.Name);
            Assert.NotNull(appliedIndex.AppliedAt);
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueNotDeletedIndexViolation_ShouldBeTrackedInCkType()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var tenantRepository = tenantContext.GetTenantRepository();
            var modelName = Constants.SimpleModelName;

            // Step 1: Create model without index
            var modelWithoutIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), IndexTypeDto.None);
            await tenantContext.ImportCkModelAsync(modelWithoutIndex);

            // Step 2: Add duplicate data (both NOT deleted)
            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var typeId = GetSimpleTypeId(modelName);
            var entity1 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity1.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity1);

            var entity2 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity2);

            await session.CommitTransactionAsync();

            // Step 3: Import new version with UniqueNotDeleted index
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName, "2.0.0"), IndexTypeDto.UniqueNotDeleted);

            // Act
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert: Index should fail due to duplicates
            var session2 = tenantRepository.GetSession();
            var ckTypeId = GetSimpleTypeId(GetModelVersion(modelName, "2.0.0"));
            var result = await tenantRepository.GetCkTypeAsync(session2, new List<CkId<CkTypeId>> { ckTypeId }, RtEntityQueryOptions.Create());

            var ckType = result.Items.FirstOrDefault();
            Assert.NotNull(ckType);
            Assert.NotNull(ckType.IndexStates);
            Assert.NotEmpty(ckType.IndexStates);

            var failedIndex = ckType.IndexStates.FirstOrDefault(s => s.State == IndexState.Failed);
            Assert.NotNull(failedIndex);
            Assert.NotNull(failedIndex.ErrorMessage);
            Assert.Contains("duplicate", failedIndex.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueNotDeletedIndex_WithArchivedEntity_ShouldSucceed()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var tenantRepository = tenantContext.GetTenantRepository();
            var modelName = Constants.SimpleModelName;

            // Step 1: Create model without index
            var modelWithoutIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), IndexTypeDto.None);
            await tenantContext.ImportCkModelAsync(modelWithoutIndex);

            // Step 2: Add duplicate data
            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var ckTypeId = GetSimpleTypeId(modelName);
            var rtCkTypeId = GetSimpleTypeId(modelName).ToRtCkId();

            var entity1 = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(rtCkTypeId);

            entity1.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity1);

            var entity2 = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(rtCkTypeId);
            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity2);

            await session.CommitTransactionAsync();

            // Step 3: Delete one of the duplicate entities
            using var session2 = await tenantRepository.GetSessionAsync();
            session2.StartTransaction();

            entity1.RtState = RtState.Archived;

            var u = new RtEntity(rtCkTypeId, entity1.RtId) { RtState = RtState.Archived };

            await tenantRepository.UpdateOneRtEntityByIdAsync(session2, rtCkTypeId, entity1.RtId, u);
            await session2.CommitTransactionAsync();

            // Step 4: Import new version with UniqueNotDeleted index
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName, "2.0.0"), IndexTypeDto.UniqueNotDeleted);

            // Act
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert: Index should succeed because one entity is deleted
            var session3 = tenantRepository.GetSession();
            var ckTypeIdV2 = GetSimpleTypeId(GetModelVersion(modelName, "2.0.0"));
            var result = await tenantRepository.GetCkTypeAsync(session3, new List<CkId<CkTypeId>> { ckTypeIdV2 }, RtEntityQueryOptions.Create());

            var ckType = result.Items.FirstOrDefault();
            Assert.NotNull(ckType);
            Assert.NotNull(ckType.IndexStates);
            Assert.NotEmpty(ckType.IndexStates);

            // The index should be successfully applied (not failed)
            var appliedIndex = ckType.IndexStates.FirstOrDefault(s => s.State == IndexState.Applied);
            Assert.NotNull(appliedIndex);
            Assert.NotNull(appliedIndex.Name);
            Assert.NotNull(appliedIndex.AppliedAt);
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueNotDeletedIndex_OnTypeUnderAbstractCollectionRoot_ShouldBeEnforced()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var modelVersion = GetModelVersion(Constants.AbstractRootModelName);

            // Act
            await tenantContext.ImportCkModelAsync(CreateAbstractRootModel(modelVersion, includeConcreteChild: true));

            // Assert
            await AssertConcreteChildUniqueIndexEnforcedAsync(tenantContext.GetTenantRepository(),
                new CkId<CkTypeId>($"{modelVersion}/{Constants.ConcreteChildTypeName}"));
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueNotDeletedIndex_OnTypeFromAnotherModelUnderAbstractCollectionRoot_ShouldBeEnforced()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var rootModelVersion = GetModelVersion(Constants.AbstractRootModelName);
            var childModelVersion = GetModelVersion(Constants.ConcreteChildModelName);

            // Arrange - The root's model is imported first, as a blueprint imports Basic before Basic.Energy
            await tenantContext.ImportCkModelAsync(CreateAbstractRootModel(rootModelVersion, includeConcreteChild: false));

            // Act
            await tenantContext.ImportCkModelAsync(CreateConcreteChildModel(childModelVersion, rootModelVersion));

            // Assert
            await AssertConcreteChildUniqueIndexEnforcedAsync(tenantContext.GetTenantRepository(),
                new CkId<CkTypeId>($"{childModelVersion}/{Constants.ConcreteChildTypeName}"));
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UpdateIndexes_WithDuplicatedInheritanceOfDerivedType_ShouldSucceed()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var modelVersion = GetModelVersion(Constants.AbstractRootModelName);
            var childTypeId = new CkId<CkTypeId>($"{modelVersion}/{Constants.ConcreteChildTypeName}");
            await tenantContext.ImportCkModelAsync(CreateAbstractRootModel(modelVersion, includeConcreteChild: true));

            // Arrange - A second, identical inheritance record, as left behind by a tenant restore
            var inheritances = GetTenantMongoDatabase(tenantId).GetCollection<BsonDocument>("CkTypeInheritance");
            var inheritance = await inheritances
                .Find(Builders<BsonDocument>.Filter.Eq("inheritorCkTypeId", childTypeId.ToString()))
                .SingleAsync(TestContext.Current.CancellationToken);
            inheritance["_id"] = ObjectId.GenerateNewId();
            await inheritances.InsertOneAsync(inheritance, cancellationToken: TestContext.Current.CancellationToken);

            // Act
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenantContext.UpdateIndexesAsync(adminSession);
            }

            // Assert
            await AssertConcreteChildUniqueIndexEnforcedAsync(tenantContext.GetTenantRepository(), childTypeId);
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task SystemIndexes_OnAbstractCollectionRootWithoutDeclaredIndexes_ShouldBeCreated()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);

            // Act
            await tenantContext.ImportCkModelAsync(
                CreateAbstractRootModel(GetModelVersion(Constants.AbstractRootModelName), includeConcreteChild: false));

            // Assert
            var indexNames = await GetAbstractRootIndexNamesAsync(tenantId);
            Assert.Contains("SystemEntity_0", indexNames);
            Assert.Contains("SystemEntity_1", indexNames);
            Assert.Contains("SystemEntity_2", indexNames);
            Assert.Contains(indexNames, n => n.EndsWith("_9000"));
            Assert.Contains(indexNames, n => n.EndsWith("_9001"));
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task ScopedIndexUpdate_ForSystemModel_ShouldReachCollectionRootsOfOtherModels()
    {
        // Arrange - Create child tenant
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            await tenantContext.ImportCkModelAsync(
                CreateAbstractRootModel(GetModelVersion(Constants.AbstractRootModelName), includeConcreteChild: false));

            // Arrange - The root's collection misses an index declared by System/Entity, as after a System model upgrade
            var collectionName = await GetAbstractRootCollectionNameAsync(tenantId);
            await GetTenantMongoDatabase(tenantId).GetCollection<BsonDocument>(collectionName).Indexes
                .DropOneAsync("SystemEntity_0", TestContext.Current.CancellationToken);

            // Act
            var databaseName = tenantId.ToLowerInvariant();
            var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
                _fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName), databaseName,
                tenantId);
            using (var session = await dataSource.CreateSessionAsync())
            {
                session.StartTransaction();
                await dataSource.UpdateIndexAsync(session, false, SystemCkIds.CkModelId,
                    TestContext.Current.CancellationToken);
                await session.CommitTransactionAsync();
            }

            // Assert
            Assert.Contains("SystemEntity_0", await GetAbstractRootIndexNamesAsync(tenantId));
        }
        finally
        {
            // Cleanup - Delete child tenant
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    private async Task<string> GetAbstractRootCollectionNameAsync(string tenantId)
    {
        var collectionNames = await (await GetTenantMongoDatabase(tenantId)
            .ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        return collectionNames.Single(n => n.EndsWith(Constants.AbstractRootTypeName));
    }

    private async Task<List<string>> GetAbstractRootIndexNamesAsync(string tenantId)
    {
        var collectionName = await GetAbstractRootCollectionNameAsync(tenantId);
        var indexes = await (await GetTenantMongoDatabase(tenantId).GetCollection<BsonDocument>(collectionName).Indexes
            .ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
        return indexes.Select(i => i["name"].AsString).ToList();
    }

    private IMongoDatabase GetTenantMongoDatabase(string tenantId)
    {
        var config = _fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(tenantId.ToLowerInvariant());
    }

    /// <summary>
    /// Asserts that the UniqueNotDeleted index of the concrete child is applied in the abstract root's collection:
    /// a unique value is accepted and a duplicate is rejected
    /// </summary>
    private static async Task AssertConcreteChildUniqueIndexEnforcedAsync(ITenantRepository tenantRepository,
        CkId<CkTypeId> childTypeId)
    {
        var session = tenantRepository.GetSession();
        var result = await tenantRepository.GetCkTypeAsync(session, new List<CkId<CkTypeId>> { childTypeId },
            RtEntityQueryOptions.Create());

        var ckType = result.Items.SingleOrDefault();
        Assert.NotNull(ckType);
        Assert.NotNull(ckType.IndexStates);
        var appliedIndex = ckType.IndexStates.FirstOrDefault(s => s.State == IndexState.Applied);
        Assert.NotNull(appliedIndex);
        Assert.EndsWith(Constants.AbstractRootTypeName, appliedIndex.CollectionName);

        var rtChildTypeId = childTypeId.ToRtCkId();

        using (var insertSession = await tenantRepository.GetSessionAsync())
        {
            insertSession.StartTransaction();
            var first = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(rtChildTypeId);
            first.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(insertSession, first);
            var second = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(rtChildTypeId);
            second.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.UniqueValue);
            await tenantRepository.InsertOneRtEntityAsync(insertSession, second);
            await insertSession.CommitTransactionAsync();
        }

        using var duplicateSession = await tenantRepository.GetSessionAsync();
        duplicateSession.StartTransaction();
        var duplicate = await tenantRepository.CreateTransientRtEntityByRtCkIdAsync(rtChildTypeId);
        duplicate.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            tenantRepository.InsertOneRtEntityAsync(duplicateSession, duplicate));
        Assert.Contains("duplicate", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a CK model whose collection root is abstract, optionally with a concrete child declaring a UniqueNotDeleted index
    /// </summary>
    private static CkCompiledModelRoot CreateAbstractRootModel(string modelVersion, bool includeConcreteChild)
    {
        var rootType = new CkCompiledTypeDto
        {
            TypeId = new CkTypeId(Constants.AbstractRootTypeName),
            DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
            IsAbstract = true,
            IsCollectionRoot = true
        };

        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId(Constants.SimpleFieldName),
                    ValueType = AttributeValueTypesDto.String
                },
            ],
            Types = includeConcreteChild
                ? [rootType, CreateConcreteChildType(modelVersion, modelVersion)]
                : [rootType]
        };
    }

    /// <summary>
    /// Creates a CK model holding only the concrete child, derived from the abstract root of another model
    /// </summary>
    private static CkCompiledModelRoot CreateConcreteChildModel(string modelVersion, string rootModelVersion)
    {
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId, new CkModelId(rootModelVersion)],
            Attributes =
            [
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId(Constants.SimpleFieldName),
                    ValueType = AttributeValueTypesDto.String
                },
            ],
            Types = [CreateConcreteChildType(modelVersion, rootModelVersion)]
        };
    }

    private static CkCompiledTypeDto CreateConcreteChildType(string modelVersion, string rootModelVersion)
    {
        return new CkCompiledTypeDto
        {
            TypeId = new CkTypeId(Constants.ConcreteChildTypeName),
            DerivedFromCkTypeId = new CkId<CkTypeId>($"{rootModelVersion}/{Constants.AbstractRootTypeName}"),
            Attributes =
            [
                new() { CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{Constants.SimpleFieldName}"), AttributeName = Constants.SimpleFieldName },
            ],
            Indexes =
            [
                new()
                {
                    IndexType = IndexTypeDto.UniqueNotDeleted,
                    Fields = [new() { AttributePaths = [Constants.SimpleFieldName] }]
                },
            ]
        };
    }

    /// <summary>
    /// Creates a minimal CK model with 1 type and 1 string attribute
    /// </summary>
    private static CkCompiledModelRoot CreateSimpleModel(string modelName, string modelVersion, bool hasUniqueIndex)
    {
        return CreateSimpleModel(modelName, modelVersion, hasUniqueIndex ? IndexTypeDto.Unique : IndexTypeDto.None);
    }

    /// <summary>
    /// Creates a minimal CK model with 1 type and 1 string attribute with specified index type
    /// </summary>
    private static CkCompiledModelRoot CreateSimpleModel(string modelName, string modelVersion, IndexTypeDto indexType)
    {
        var model = new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId(Constants.SimpleFieldName),
                    ValueType = AttributeValueTypesDto.String
                },
            ],
            Types =
            [
                new()
                {
                    TypeId = new CkTypeId(Constants.SimpleTypeName),
                    DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                    IsCollectionRoot = true,
                    Attributes =
                    [
                        new() { CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{Constants.SimpleFieldName}"), AttributeName = Constants.SimpleFieldName },
                    ]
                },
            ]
        };

        // Add index if requested
        if (indexType != IndexTypeDto.None)
        {
            model.Types[0].Indexes =
            [
                new()
                {
                    IndexType = indexType,
                    Fields = [new() { AttributePaths = [Constants.SimpleFieldName] }]
                },
            ];
        }

        return model;
    }
}
