using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Tests for CK model import optimizations:
/// - UpdateCollectionsAsync with skipCleanup on second call
/// - UpdateIndexAsync scoped to the imported model only
/// - CleanupEmptyAbstractTypeCollections using fast document existence check
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkModelImportPerformanceTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");

    [Fact]
    public async Task ImportCkModel_SequentialVersionUpgrade_ShouldSucceedWithScopedIndexUpdate()
    {
        // Arrange: Reset tenant to clean state
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();

        // Act: Import v1 first - this triggers full UpdateCollectionsAsync and scoped UpdateIndexAsync
        var operationResult1 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult1);

        // Assert: v1 import succeeded
        Assert.False(operationResult1.HasErrors, "v1 import should succeed");
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV1ModelId));

        // Act: Import v2 - this also uses scoped UpdateIndexAsync (only Test model collection roots)
        var operationResult2 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV2ModelId, operationResult2);

        // Assert: v2 import succeeded (scoped index update worked correctly)
        Assert.False(operationResult2.HasErrors, "v2 import should succeed with scoped index update");
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV2ModelId));

        // Verify: Load cache to confirm model integrity after scoped operations
        await systemContext.LoadCacheForTenantAsync();
    }

    [Fact]
    public async Task ImportCkModel_ReimportSameVersion_ShouldSucceedWithSkipCleanup()
    {
        // Arrange: Reset tenant and import v1
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();

        var operationResult1 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult1);
        Assert.False(operationResult1.HasErrors);

        // Act: Re-import v1 - exercises the skipCleanup path on second UpdateCollectionsAsync
        // and scoped UpdateIndexAsync with same model
        var operationResult2 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult2);

        // Assert: Re-import should succeed
        Assert.False(operationResult2.HasErrors, "Re-import of same version should succeed");
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV1ModelId));
    }
}
