using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Logging;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="MongoRuntimeRepositoryProvider"/>.
/// These tests verify the provider's ability to access tenant repositories
/// using the MongoDB-based system context.
/// </summary>
[Collection(SystemCollection.Name)]
public class MongoRuntimeRepositoryProviderTests(SystemFixture fixture)
{
    [Fact]
    public async Task GetRepositoryAsync_NonExistentTenant_ShouldReturnNull()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var provider = CreateProvider();

        // Act
        var result = await provider.GetRepositoryAsync("non-existent-tenant", ct);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetRepositoryAsync_ExistingTenant_ShouldReturnRepository()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"t-{Guid.NewGuid():N}"[..20];

        try
        {
            // Create tenant using the proper API
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var provider = CreateProvider();

            // Act
            var result = await provider.GetRepositoryAsync(tenantId, ct);

            // Assert
            Assert.NotNull(result);
        }
        finally
        {
            // Cleanup
            try
            {
                using var session = await systemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void IsRepositoryAvailable_NonExistentTenant_ShouldReturnFalse()
    {
        // Arrange
        var provider = CreateProvider();

        // Act
        var result = provider.IsRepositoryAvailable("non-existent-tenant");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsRepositoryAvailable_ExistingTenant_ShouldReturnTrue()
    {
        // Arrange
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"t-{Guid.NewGuid():N}"[..20];

        try
        {
            // Create tenant using the proper API
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var provider = CreateProvider();

            // Act
            var result = provider.IsRepositoryAvailable(tenantId);

            // Assert
            Assert.True(result);
        }
        finally
        {
            // Cleanup
            try
            {
                using var session = await systemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task GetRepositoryAsync_MultipleCalls_ShouldReturnConsistentRepository()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"t-{Guid.NewGuid():N}"[..20];

        try
        {
            // Create tenant using the proper API
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            var provider = CreateProvider();

            // Act
            var result1 = await provider.GetRepositoryAsync(tenantId, ct);
            var result2 = await provider.GetRepositoryAsync(tenantId, ct);

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            // Both calls should return valid repositories
            Assert.Equal(result1.GetType(), result2.GetType());
        }
        finally
        {
            // Cleanup
            try
            {
                using var session = await systemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task EnsureCkModelInstalledAsync_NonExistentTenant_AddsErrorToOperationResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = CreateProvider();
        var operationResult = new OperationResult();

        await provider.EnsureCkModelInstalledAsync(
            "non-existent-tenant-xyz",
            new CkModelId("Test"),
            operationResult,
            ct);

        Assert.True(operationResult.HasErrors);
        Assert.Contains(operationResult.Messages,
            m => m.MessageNumber == 24 && m.MessageText!.Contains("non-existent-tenant-xyz"));
    }

    /// <summary>
    /// Verifies the "Ensure" contract: if <c>ImportCkModelAsync</c> completes without
    /// installing the model (the <c>ModelValidationException</c> swallow path inside
    /// the CkModelId overload of ImportCkModelAsync), <c>EnsureCkModelInstalledAsync</c>
    /// surfaces an error to the caller via <see cref="OperationResult"/>.
    /// </summary>
    [Fact]
    public async Task EnsureCkModelInstalledAsync_ImportLeavesModelMissing_AddsErrorToOperationResult()
    {
        var ct = TestContext.Current.CancellationToken;
        const string tenantId = "fake-tenant";
        var modelId = new CkModelId("MissingDep-1.0.0");

        var session = A.Fake<IOctoSession>();
        var emptyResultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => emptyResultSet.Items).Returns(Array.Empty<CkModel>());

        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetSessionAsync()).Returns(session);
        A.CallTo(() => repository.GetCkModelsAsync(
                A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult(emptyResultSet));

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);
        // ImportCkModelAsync is left unconfigured — FakeItEasy returns Task.CompletedTask,
        // mimicking the silent swallow of ModelValidationException inside the real implementation.

        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TryFindTenantContextAsync(tenantId)).Returns(tenantContext);

        var logger = A.Fake<ILogger<MongoRuntimeRepositoryProvider>>();
        var provider = new MongoRuntimeRepositoryProvider(systemContext, logger);

        var operationResult = new OperationResult();
        await provider.EnsureCkModelInstalledAsync(tenantId, modelId, operationResult, ct);

        Assert.True(operationResult.HasErrors);
        Assert.Contains(operationResult.Messages,
            m => m.MessageNumber == 25 && m.MessageText!.Contains("MissingDep"));
    }

    /// <summary>
    /// Pins the version-aware idempotency fix. Before the fix the provider skipped the
    /// import whenever ANY version of the model name was already installed; for an
    /// additive-only CK bump (no migration script) the older version stayed in the DB,
    /// <see cref="ICkModelUpgradeService"/> ran a no-op migration that only recorded
    /// history, and downstream <c>ValidateCkModels</c> failed because the new version
    /// was never materialised. The current contract: skip only when the installed
    /// version is greater than OR equal to the requested one; otherwise fall through
    /// to ImportCkModelAsync so the schema actually gets upgraded.
    /// </summary>
    [Fact]
    public async Task EnsureCkModelInstalledAsync_OlderVersionInstalled_TriggersImportForNewerVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        const string tenantId = "fake-tenant";
        var requested = new CkModelId("System.Communication-3.21.0");

        // Installed = 3.20.0 (older than requested 3.21.0).
        var olderInstalled = new CkModel
        {
            Id = new CkModelId("System.Communication-3.20.0"),
            ModelState = ModelState.Available,
        };
        var newerInstalled = new CkModel
        {
            Id = new CkModelId("System.Communication-3.21.0"),
            ModelState = ModelState.Available,
        };
        var session = A.Fake<IOctoSession>();
        var olderOnlyResultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => olderOnlyResultSet.Items).Returns(new[] { olderInstalled });
        var bothResultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => bothResultSet.Items).Returns(new[] { olderInstalled, newerInstalled });

        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetSessionAsync()).Returns(session);
        // First GetCkModelsAsync call (the version-aware pre-check) returns the older
        // installation only; the post-import verification call returns both so the
        // provider does not flag a missing model after the simulated import.
        A.CallTo(() => repository.GetCkModelsAsync(
                A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsNextFromSequence(
                Task.FromResult(olderOnlyResultSet),
                Task.FromResult(bothResultSet));

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);

        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TryFindTenantContextAsync(tenantId)).Returns(tenantContext);

        var logger = A.Fake<ILogger<MongoRuntimeRepositoryProvider>>();
        var provider = new MongoRuntimeRepositoryProvider(systemContext, logger);

        var operationResult = new OperationResult();
        await provider.EnsureCkModelInstalledAsync(tenantId, requested, operationResult, ct);

        // ImportCkModelAsync must have been called exactly once with the requested id.
        A.CallTo(() => tenantContext.ImportCkModelAsync(requested, operationResult))
            .MustHaveHappenedOnceExactly();
        Assert.False(operationResult.HasErrors);
    }

    /// <summary>
    /// Downgrade-protection: when the installed version is strictly greater than the
    /// requested one (the historic regression — blueprint engine passing the lower
    /// bound of a version range), the provider must NOT trigger ImportCkModelAsync,
    /// because the real import path would `InsertModelWithImportingState` and wipe
    /// every CkModel row sharing the name, silently downgrading the schema.
    /// </summary>
    [Fact]
    public async Task EnsureCkModelInstalledAsync_NewerVersionInstalled_SkipsImport()
    {
        var ct = TestContext.Current.CancellationToken;
        const string tenantId = "fake-tenant";
        var requested = new CkModelId("System-2.0.0"); // lower bound of [2.0,3.0)

        var newerInstalled = new CkModel
        {
            Id = new CkModelId("System-2.2.0"),
            ModelState = ModelState.Available,
        };
        var session = A.Fake<IOctoSession>();
        var resultSet = A.Fake<IResultSet<CkModel>>();
        A.CallTo(() => resultSet.Items).Returns(new[] { newerInstalled });

        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetSessionAsync()).Returns(session);
        A.CallTo(() => repository.GetCkModelsAsync(
                A<IOctoSession>._, A<List<CkModelId>?>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult(resultSet));

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetTenantRepository()).Returns(repository);

        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TryFindTenantContextAsync(tenantId)).Returns(tenantContext);

        var logger = A.Fake<ILogger<MongoRuntimeRepositoryProvider>>();
        var provider = new MongoRuntimeRepositoryProvider(systemContext, logger);

        var operationResult = new OperationResult();
        await provider.EnsureCkModelInstalledAsync(tenantId, requested, operationResult, ct);

        A.CallTo(() => tenantContext.ImportCkModelAsync(A<CkModelId>._, A<OperationResult>._))
            .MustNotHaveHappened();
        Assert.False(operationResult.HasErrors);
    }

    private IRuntimeRepositoryProvider CreateProvider()
    {
        fixture.EnsureInitialized();
        var systemContext = fixture.GetSystemContext();
        var logger = fixture.GetService<ILogger<MongoRuntimeRepositoryProvider>>();
        return new MongoRuntimeRepositoryProvider(systemContext, logger);
    }
}
