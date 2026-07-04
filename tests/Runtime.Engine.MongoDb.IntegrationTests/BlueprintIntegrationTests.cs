using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for MongoDB blueprint functionality.
/// </summary>
[Collection(BlueprintCollection.Name)]
public class BlueprintIntegrationTests(BlueprintFixture fixture)
{
    private readonly BlueprintFixture _fixture = fixture;

    #region TenantContext Blueprint Integration Tests

    [Fact]
    public async Task CreateChildTenantWithNullBlueprint_CreatesNormalTenant()
    {
        // Arrange
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"no-blueprint-tenant-{Guid.NewGuid():N}";

        try
        {
            // Act
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            var result = await systemContext.CreateChildTenantAsync(session, tenantId, tenantId, null);
            await session.CommitTransactionAsync();

            // Assert
            Assert.Null(result); // No blueprint was applied

            using var session2 = await systemContext.GetAdminSessionAsync();
            session2.StartTransaction();
            var tenantExists = await systemContext.IsChildTenantExistingAsync(session2, tenantId);
            await session2.CommitTransactionAsync();

            Assert.True(tenantExists);
        }
        finally
        {
            // Cleanup
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            try
            {
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #endregion
}
