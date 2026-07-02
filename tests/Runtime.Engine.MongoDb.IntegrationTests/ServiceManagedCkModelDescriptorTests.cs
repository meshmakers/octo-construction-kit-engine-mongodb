using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Covers the shared embedded-import-with-downgrade-guard helper that backs both the StreamData
/// descriptor and the generic <see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.IServiceManagedCkModelDescriptor" />
/// auto-import path. Reuses the migration fixture (Test v1 + v2 catalog).
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class ImportEmbeddedCkModelWithDowngradeGuardTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");

    [Fact]
    public async Task FreshTenant_ImportsEmbeddedVersion()
    {
        await fixture.ResetTenantAsync();
        var tenantContext = (TenantContext)fixture.GetSystemContext();

        await tenantContext.ImportEmbeddedCkModelWithDowngradeGuardAsync(TestV2ModelId);

        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));
    }

    [Fact]
    public async Task LowerVersionInstalled_UpgradesToEmbeddedVersion()
    {
        await fixture.ResetTenantAsync();
        var tenantContext = (TenantContext)fixture.GetSystemContext();

        // Arrange: tenant sits on the older version.
        await tenantContext.ImportCkModelAsync(TestV1ModelId, new OperationResult());
        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV1ModelId));

        // Act: descriptor advertises the newer embedded version.
        await tenantContext.ImportEmbeddedCkModelWithDowngradeGuardAsync(TestV2ModelId);

        // Assert: upgraded to v2 (v1 replaced), and MigrationHistory tracks 2.0.0.
        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));
        Assert.False(await tenantContext.IsCkModelExistingAsync(TestV1ModelId));

        var upgradeService = fixture.GetService<ICkModelUpgradeService>();
        var installed = await upgradeService.GetInstalledVersionsAsync(
            tenantContext.TenantId, TestContext.Current.CancellationToken);
        Assert.True(installed.TryGetValue("Test", out var version));
        Assert.Equal("2.0.0", version);
    }

    [Fact]
    public async Task HigherVersionInstalled_SkipsDowngrade()
    {
        await fixture.ResetTenantAsync();
        var tenantContext = (TenantContext)fixture.GetSystemContext();

        // Arrange: tenant already on the newer version (e.g. a sibling deploy shipped it).
        await tenantContext.ImportCkModelAsync(TestV2ModelId, new OperationResult());
        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));

        // Act: an older embedded target must NOT clobber the newer installed schema.
        await tenantContext.ImportEmbeddedCkModelWithDowngradeGuardAsync(TestV1ModelId);

        // Assert: v2 preserved, v1 was not imported.
        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));
        Assert.False(await tenantContext.IsCkModelExistingAsync(TestV1ModelId));
    }
}

/// <summary>
/// Covers the DI-driven <see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.IServiceManagedCkModelDescriptor" />
/// path: a registered descriptor is auto-imported at its embedded version on tenant resolve, and the
/// per-process guard makes a repeat attempt a no-op.
/// </summary>
[Collection(ServiceManagedDescriptorCollection.Name)]
public class ServiceManagedCkModelDescriptorTests(ServiceManagedDescriptorFixture fixture)
{
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");

    [Fact]
    public async Task EnsureServiceManagedCkModelsImported_ImportsRegisteredDescriptorAtEmbeddedVersion()
    {
        await fixture.ResetTenantAsync();
        TenantContext.ResetServiceManagedCkModelImportGuardForTests();
        var tenantContext = (TenantContext)fixture.GetSystemContext();

        await tenantContext.EnsureServiceManagedCkModelsImportedAsync();

        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));
    }

    [Fact]
    public async Task EnsureServiceManagedCkModelsImported_SecondAttempt_IsGuardedNoOp()
    {
        await fixture.ResetTenantAsync();
        TenantContext.ResetServiceManagedCkModelImportGuardForTests();
        var tenantContext = (TenantContext)fixture.GetSystemContext();

        // First attempt imports and arms the per-process guard.
        await tenantContext.EnsureServiceManagedCkModelsImportedAsync();
        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));

        // Wipe the tenant but DON'T reset the guard: the second attempt must skip (no re-import).
        await fixture.ResetTenantAsync();
        await tenantContext.EnsureServiceManagedCkModelsImportedAsync();
        Assert.False(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));

        // Clearing the guard re-enables the import.
        TenantContext.ResetServiceManagedCkModelImportGuardForTests();
        await tenantContext.EnsureServiceManagedCkModelsImportedAsync();
        Assert.True(await tenantContext.IsCkModelExistingAsync(TestV2ModelId));
    }
}
