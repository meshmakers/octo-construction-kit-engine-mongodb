using Meshmakers.Octo.Runtime.Contracts.MongoDb.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class GenerateSampleDataFixture: ImportTestCkModelFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var importRtModelCommand = Provider.GetRequiredService<IImportRtModelCommand>();
        await importRtModelCommand.Import("octosystem", "testData/sampleRtModel.yaml", "text/yaml");
    }

    public override Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}