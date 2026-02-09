using Meshmakers.Octo.Runtime.Contracts;
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

    private IRuntimeRepositoryProvider CreateProvider()
    {
        fixture.EnsureInitialized();
        var systemContext = fixture.GetSystemContext();
        var logger = fixture.GetService<ILogger<MongoRuntimeRepositoryProvider>>();
        return new MongoRuntimeRepositoryProvider(systemContext, logger);
    }
}
