using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class IndexCreationTests(ImportTestCkModelFixture fixture) : IClassFixture<ImportTestCkModelFixture>
{
    [Fact]
    public async Task SuccessfulIndexCreation_ShouldBeTrackedInCkType()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        // Act - No need to create indexes manually, they will be created automatically on

        // Get a CkType that should have indexes (System/Entity is a good candidate)
        var session = tenantRepository.GetSession();
        var ckTypeId = new CkId<CkTypeId>("System/Entity");
        var result = await tenantRepository.GetCkTypeAsync(
            session,
            null,
            new List<CkId<CkTypeId>> { ckTypeId },
            DataQueryOperation.Create(),
            null,
            null);

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

    [Fact]
    public async Task UniqueIndexViolation_ShouldBeTrackedInCkType()
    {
        // Arrange - Start the System Tenant (done by fixture)
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var modelName = $"SimpleModel";

        // Step 1: Create a simple CK model with 1 type and 1 attribute (no index yet)
        var modelWithoutIndex = CreateSimpleModel(modelName, $"{modelName}-1.0.0", hasUniqueIndex: false);
        await systemContext.ImportCkModelAsync(modelWithoutIndex);

        // Step 2: Add Sample Data That Violates the Unique index constraints
        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Insert two entities with duplicate SimpleField values
        var entity1 = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>($"{modelName}/SimpleType"));
        entity1.SetAttributeValue("SimpleField", AttributeValueTypesDto.String, "DuplicateValue");
        await tenantRepository.InsertOneRtEntityAsync(session, entity1);

        var entity2 = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>($"{modelName}/SimpleType"));
        entity2.SetAttributeValue("SimpleField", AttributeValueTypesDto.String, "DuplicateValue"); // Same value as entity1
        await tenantRepository.InsertOneRtEntityAsync(session, entity2);

        await session.CommitTransactionAsync();

        // Step 3: Modify the ConstructionKit to add the violating unique index
        // Import the SAME model but with a unique index - this will update the existing model
        var modelWithIndex = CreateSimpleModel(modelName, $"{modelName}-1.0.0", hasUniqueIndex: true);

        // Act - Apply this new ConstructionKit
        await systemContext.ImportCkModelAsync(modelWithIndex);

        // Assert - Verify a) It didn't fail b) the unique indices that failed are available via CkType
        var session2 = tenantRepository.GetSession();
        var ckTypeId = new CkId<CkTypeId>($"{modelName}/SimpleType");
        var result = await tenantRepository.GetCkTypeAsync(
            session2,
            null,
            new List<CkId<CkTypeId>> { ckTypeId },
            DataQueryOperation.Create(),
            null,
            null);

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

    /// <summary>
    /// Creates a minimal CK model with 1 type and 1 string attribute
    /// </summary>
    private static CkCompiledModelRoot CreateSimpleModel(string modelName, string modelVersion, bool hasUniqueIndex)
    {
        var model = new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = new List<CkModelId> { new CkModelId("System-1.0.0") },
            Attributes = new List<CkAttributeDto>
            {
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("SimpleField"),
                    ValueType = AttributeValueTypesDto.String
                }
            },
            Types = new List<CkCompiledTypeDto>
            {
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("SimpleType"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>("System/Entity"),
                    IsCollectionRoot = true,
                    Attributes = new List<CkTypeAttributeDto>
                    {
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>($"{modelName}/SimpleField"),
                            AttributeName = "SimpleField"
                        }
                    }
                }
            }
        };

        // Add unique index if requested
        if (hasUniqueIndex)
        {
            model.Types[0].Indexes = new List<CkTypeIndexDto>
            {
                new CkTypeIndexDto
                {
                    IndexType = IndexTypeDto.Unique,
                    Fields = new List<CkIndexFieldsDto>
                    {
                        new CkIndexFieldsDto
                        {
                            AttributePaths = new List<string> { "SimpleField" }
                        }
                    }
                }
            };
        }

        return model;
    }
}
