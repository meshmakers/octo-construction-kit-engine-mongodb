using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class CkCacheInvalidationAfterImportTests(CkModelImportMigrationFixture fixture)
    : IClassFixture<CkModelImportMigrationFixture>
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");

    [Fact]
    public async Task ImportCkModel_ByCkModelId_ShouldInvalidateCache()
    {
        // Arrange: Reset tenant state (System model only)
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();
        var cacheService = fixture.GetService<ICkCacheService>();
        var tenantId = systemContext.TenantId;

        // Ensure cache is unloaded before import
        if (cacheService.IsTenantLoaded(tenantId))
        {
            cacheService.Unload(tenantId);
        }

        // Act: Import Test-1.0.0 via CkModelId overload
        // This triggers RunCkModelMigrationsForImportAsync internally,
        // which triggers ModelLoaderService.LoadAsync -> cache loads
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);

        // Assert: Cache should have been unloaded after import
        Assert.False(cacheService.IsTenantLoaded(tenantId),
            "Cache should be unloaded after CK model import to ensure fresh reload with all models");
    }

    [Fact]
    public async Task ImportCkModel_ByCompiledModelRoot_ShouldInvalidateCache()
    {
        // Arrange: Reset tenant state (System model only)
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();
        var cacheService = fixture.GetService<ICkCacheService>();
        var tenantId = systemContext.TenantId;

        // Ensure cache is unloaded before import
        if (cacheService.IsTenantLoaded(tenantId))
        {
            cacheService.Unload(tenantId);
        }

        // Get compiled model from catalog
        var catalogService = fixture.GetService<ICatalogService>();
        var opResult = new OperationResult();
        var compiledModel = await catalogService.GetAsync(TestV1ModelId, opResult);
        Assert.NotNull(compiledModel);
        Assert.False(opResult.HasErrors);

        // Act: Import via CkCompiledModelRoot overload
        await systemContext.ImportCkModelAsync(compiledModel);

        // Assert: Cache should have been unloaded after import
        Assert.False(cacheService.IsTenantLoaded(tenantId),
            "Cache should be unloaded after CK model import to ensure fresh reload with all models");
    }

    [Fact]
    public async Task ImportCkModel_SequentialImports_CacheContainsAllModels()
    {
        // Arrange: Reset tenant state (System model only)
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();
        var cacheService = fixture.GetService<ICkCacheService>();
        var tenantId = systemContext.TenantId;

        // Ensure cache is unloaded before imports
        if (cacheService.IsTenantLoaded(tenantId))
        {
            cacheService.Unload(tenantId);
        }

        // Act: Import Test-1.0.0, then Test-2.0.0 sequentially
        var operationResult1 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult1);
        Assert.False(operationResult1.HasErrors);

        var operationResult2 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV2ModelId, operationResult2);
        Assert.False(operationResult2.HasErrors);

        // Trigger cache reload
        await systemContext.LoadCacheForTenantAsync();

        // Assert: Cache is now loaded with all models
        Assert.True(cacheService.IsTenantLoaded(tenantId),
            "Cache should be loaded after LoadCacheForTenantAsync");

        // Assert: Cache contains the Test model's types (verifies all models are present)
        var ckTypeGraph = cacheService.GetRtCkType(tenantId, TestCkIds.RtCkDistrictTypeId);
        Assert.NotNull(ckTypeGraph);
    }
}
