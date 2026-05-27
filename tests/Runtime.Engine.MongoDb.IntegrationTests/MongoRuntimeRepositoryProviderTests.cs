using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="MongoRuntimeRepositoryProvider"/>.
/// These tests verify the provider's ability to access tenant repositories
/// using the MongoDB-based system context.
/// </summary>
[Collection("Sequential")]
public class MongoRuntimeRepositoryProviderTests(SystemFixture fixture)
    : IClassFixture<SystemFixture>
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

    private IRuntimeRepositoryProvider CreateProvider()
    {
        fixture.EnsureInitialized();
        var systemContext = fixture.GetSystemContext();
        var logger = fixture.GetService<ILogger<MongoRuntimeRepositoryProvider>>();
        return new MongoRuntimeRepositoryProvider(systemContext, logger);
    }
}
