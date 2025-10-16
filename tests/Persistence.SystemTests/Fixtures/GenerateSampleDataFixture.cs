using Meshmakers.Octo.Runtime.Contracts.Exchange;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class GenerateSampleDataFixture : ImportTestCkModelFixture
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
