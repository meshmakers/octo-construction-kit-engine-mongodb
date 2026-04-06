using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for migration support tests. Imports the Test CK model and sample RT data
/// to provide a known state for testing CK-cache-free migration operations.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class MigrationSupportFixture : ImportTestCkModelFixture
{
    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var importRtModelCommand = GetService<IImportRtModelCommand>();
        var systemContext = GetSystemContext();
        var repository = systemContext.GetSystemTenantRepository();
        await importRtModelCommand.ImportAsync(repository, "testData/sampleRtModel.yaml",
            ExchangeMimeTypes.MimeTypeYaml, ImportStrategy.Insert);
    }
}
