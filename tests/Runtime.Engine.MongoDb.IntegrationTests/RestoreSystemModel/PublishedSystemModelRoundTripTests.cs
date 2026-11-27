using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

using Xunit;

using static Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RestoreSystemModel.RestoreSystemModelFixture;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RestoreSystemModel;

/// <summary>
///     The restore check compares a tenant's service-managed models with the catalog in canonical form, so a genuine
///     published model must read back from MongoDB to exactly its published canonical form - otherwise every restore of
///     a genuine dump would be refused. Pinned with the real published System.Identity 3.8.0 and the engine's System.
/// </summary>
[Collection(RestoreSystemModelCollection.Name)]
public class PublishedSystemModelRoundTripTests(RestoreSystemModelFixture fixture)
{
    [Fact]
    public async Task PublishedSystemIdentity_ReadsBackToItsPublishedContent()
    {
        var published = await fixture.Catalog.LoadAsync(Identity380);
        var systemContext = (TenantContext)fixture.GetSystemContext();
        var models = await DatabaseCkModelRepository.ReadServiceManagedModelsAsync(
            systemContext.CreateAdminDataSourceForRestore(systemContext.DatabaseName, systemContext.TenantId));
        var readBack = models.Single(m => m.Row.Id == Identity380).Compiled;

        var differences = CkModelJsonComparer.Compare(published, readBack);
        Assert.True(differences.Count == 0, string.Join("\n", differences));
        Assert.True(RestoreSystemModelGuard.IsSameContent(readBack, published));
    }

    [Fact]
    public async Task EngineSystemModel_ReadsBackToItsCatalogContent()
    {
        var systemContext = (TenantContext)fixture.GetSystemContext();
        var models = await DatabaseCkModelRepository.ReadServiceManagedModelsAsync(
            systemContext.CreateAdminDataSourceForRestore(systemContext.DatabaseName, systemContext.TenantId));
        var system = models.Single(m => m.Row.Id.Name == "System");
        var published = await fixture.GetService<ICatalogService>().GetAsync(system.Row.Id, new OperationResult());

        Assert.Empty(CkModelJsonComparer.Compare(published!, system.Compiled));
    }
}
