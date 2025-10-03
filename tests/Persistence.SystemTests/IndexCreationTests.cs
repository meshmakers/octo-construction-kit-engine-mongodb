using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
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
}
