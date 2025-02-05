using Meshmakers.Octo.Runtime.Contracts.MongoDb.Exchange;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class GenerateSampleDataFixture: ImportTestCkModelFixture
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var importRtModelCommand = Provider.GetRequiredService<IImportRtModelCommand>();
        await importRtModelCommand.ImportAsync("octosystem", "testData/sampleRtModel.yaml", Constants.MimeTypeYaml);
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
