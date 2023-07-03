using Xunit;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

public class BaseOperationTests: IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;

    public BaseOperationTests(SystemFixture systemFixture)
    {
        _systemFixture = systemFixture;
    }

    [Fact]
    public async void CreateFirstTenant()
    {
        var systemContext = _systemFixture.GetSystemContext();
        await systemContext.CreateSystemDatabaseAsync();
    }
}