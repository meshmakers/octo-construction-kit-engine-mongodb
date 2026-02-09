using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class CkModelImportMigrationTests(CkModelImportMigrationFixture fixture)
    : IClassFixture<CkModelImportMigrationFixture>
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");

    [Fact]
    public async Task ImportCkModel_HigherVersion_ShouldTriggerMigration()
    {
        // Arrange: Import v1 first
        var systemContext = fixture.GetSystemContext();
        var operationResult = new OperationResult();

        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV1ModelId));

        // Act: Import v2 (higher version) - this should trigger migration
        var operationResult2 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV2ModelId, operationResult2);

        // Assert: v2 model exists and import succeeded
        Assert.False(operationResult2.HasErrors);
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV2ModelId));

        // Verify migration was tracked via ICkModelUpgradeService
        var upgradeService = fixture.GetService<ICkModelUpgradeService>();
        var tenantId = systemContext.TenantId;
        var installedVersions = await upgradeService.GetInstalledVersionsAsync(
            tenantId, TestContext.Current.CancellationToken);

        // The installed version for "Test" should be 2.0.0
        Assert.True(installedVersions.TryGetValue("Test", out var version),
            "Test model should be tracked in installed versions");
        Assert.Equal("2.0.0", version);
    }

    [Fact]
    public async Task ImportCkModel_SameVersion_ShouldNotTriggerMigration()
    {
        // Arrange: Import v1
        var systemContext = fixture.GetSystemContext();
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);

        // Act: Import v1 again (same version)
        var operationResult2 = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult2);

        // Assert: No errors and import was either skipped or no-op
        Assert.False(operationResult2.HasErrors);
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV1ModelId));
    }

    [Fact]
    public async Task ImportCkModel_FirstInstall_ShouldRecordVersionWithoutMigration()
    {
        // Arrange: Fresh tenant (system tenant already created by fixture)
        var systemContext = fixture.GetSystemContext();

        // Act: Import v1 into fresh tenant (no previous version exists)
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);

        // Assert: Import succeeded
        Assert.False(operationResult.HasErrors);
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV1ModelId));
    }

    [Fact]
    public async Task ImportCkModel_WithCompiledModelRoot_HigherVersion_ShouldTriggerMigration()
    {
        // Arrange: Import v1 first via CkModelId overload
        var systemContext = fixture.GetSystemContext();
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);

        // Get compiled model root for v2
        var catalogService = fixture.GetService<ICatalogService>();
        var opResult = new OperationResult();
        var v2CompiledModel = await catalogService.GetAsync(TestV2ModelId, opResult);
        Assert.NotNull(v2CompiledModel);
        Assert.False(opResult.HasErrors);

        // Act: Import v2 via CkCompiledModelRoot overload
        await systemContext.ImportCkModelAsync(v2CompiledModel);

        // Assert: v2 model exists
        Assert.True(await systemContext.IsCkModelExistingAsync(TestV2ModelId));
    }
}
