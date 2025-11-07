using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class IndexCreationTests : IClassFixture<ImportTestCkModelFixture>
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
    }

    private static CkId<CkTypeId> GetSimpleTypeId(string modelName) => new($"{modelName}/{Constants.SimpleTypeName}");

    private static CkId<CkAttributeId> GetSimpleFieldId(string modelName) =>
        new($"{modelName}/{Constants.SimpleFieldName}");

    private static string GetModelVersion(string modelName) => $"{modelName}-1.0.0";
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
                null,
                new List<CkId<CkTypeId>> { SystemCkIds.CkEntityTypeId },
                RtEntityQueryOptions.Create());

            // Assert
            var ckType = result.Items.FirstOrDefault();
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
            var session = await tenantRepository.GetSessionAsync();
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
            // Import the SAME model but with a unique index - this will update the existing model
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), hasUniqueIndex: true);

            // Act - Apply this new ConstructionKit
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert - Verify a) It didn't fail b) the unique indices that failed are available via CkType
            var session2 = tenantRepository.GetSession();
            var ckTypeId = GetSimpleTypeId(modelName);
            var result = await tenantRepository.GetCkTypeAsync(
                session2,
                null,
                new List<CkId<CkTypeId>> { ckTypeId },
                RtEntityQueryOptions.Create());

            var r = await tenantRepository.GetRtEntitiesByTypeAsync(session2, typeId.ToRtCkId(), RtEntityQueryOptions.Create());

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

            var session = await tenantRepository.GetSessionAsync();
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
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), hasUniqueIndex: true);
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Verify index failed
            var session2 = tenantRepository.GetSession();
            var ckTypeId = GetSimpleTypeId(modelName);
            var result = await tenantRepository.GetCkTypeAsync(session2, null, new List<CkId<CkTypeId>> { ckTypeId }, RtEntityQueryOptions.Create());
            var ckType = result.Items.FirstOrDefault();
            Assert.NotNull(ckType);
            var failedIndex = ckType.IndexStates?.FirstOrDefault(s => s.State == IndexState.Failed);
            Assert.NotNull(failedIndex);

            // Step 3: Fix the data - update entity2 to have different value
            var session3 = await tenantRepository.GetSessionAsync();
            session3.StartTransaction();

            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.UniqueValue);
            await tenantRepository.UpdateOneRtEntityByIdAsync(session3, ckTypeId.ToRtCkId(), entity2.RtId, entity2);

            await session3.CommitTransactionAsync();

            // Act: Re-import the model with unique index
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert: Index should now be successfully applied
            var session4 = tenantRepository.GetSession();
            var result2 = await tenantRepository.GetCkTypeAsync(session4, null, new List<CkId<CkTypeId>> { ckTypeId }, RtEntityQueryOptions.Create());
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
            var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var typeId = GetSimpleTypeId(modelName);
            var entity1 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity1.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity1);

            var entity2 = await tenantRepository.CreateTransientRtEntityAsync(typeId);
            entity2.SetAttributeValue(Constants.SimpleFieldName, AttributeValueTypesDto.String, Constants.DuplicateValue);
            await tenantRepository.InsertOneRtEntityAsync(session, entity2);

            await session.CommitTransactionAsync();

            // Step 3: Import with UniqueNotDeleted index
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), IndexTypeDto.UniqueNotDeleted);

            // Act
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert: Index should fail due to duplicates
            var session2 = tenantRepository.GetSession();
            var ckTypeId = GetSimpleTypeId(modelName);
            var result = await tenantRepository.GetCkTypeAsync(session2, null, new List<CkId<CkTypeId>> { ckTypeId }, RtEntityQueryOptions.Create());

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
    public async Task UniqueNotDeletedIndex_WithDeletedEntity_ShouldSucceed()
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
            var session = await tenantRepository.GetSessionAsync();
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
            var session2 = await tenantRepository.GetSessionAsync();
            session2.StartTransaction();

            entity1.RtState = RtState.Deleted;

            var u = new RtEntity(rtCkTypeId, entity1.RtId) { RtState = RtState.Deleted };

            await tenantRepository.UpdateOneRtEntityByIdAsync(session2, rtCkTypeId, entity1.RtId, u);
            await session2.CommitTransactionAsync();

            // Step 4: Import with UniqueNotDeleted index
            var modelWithIndex = CreateSimpleModel(modelName, GetModelVersion(modelName), IndexTypeDto.UniqueNotDeleted);

            // Act
            await tenantContext.ImportCkModelAsync(modelWithIndex);

            // Assert: Index should succeed because one entity is deleted
            var session3 = tenantRepository.GetSession();
            var result = await tenantRepository.GetCkTypeAsync(session3, null, new List<CkId<CkTypeId>> { ckTypeId }, RtEntityQueryOptions.Create());

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
                        new() { CkAttributeId = GetSimpleFieldId(modelName), AttributeName = Constants.SimpleFieldName },
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
