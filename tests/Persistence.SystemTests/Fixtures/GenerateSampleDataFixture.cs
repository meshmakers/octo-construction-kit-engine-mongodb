using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class GenerateSampleDataFixture: ImportTestCkModelFixture
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var importRtModelCommand = Provider.GetRequiredService<IImportRtModelCommand>();
        var systemContext = GetSystemContext();
        var repository = systemContext.GetSystemTenantRepository();
        await importRtModelCommand.ImportAsync(repository, "testData/sampleRtModel.yaml", ExchangeMimeTypes.MimeTypeYaml);
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
