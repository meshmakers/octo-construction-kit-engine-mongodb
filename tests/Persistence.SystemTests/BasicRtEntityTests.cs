using Meshmakers.Octo.Backend.Persistence.SystemTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

public class BasicRtEntityTests
{
    private readonly SystemFixture _systemFixture;

    public BasicRtEntityTests(SystemFixture systemFixture)
    {
        _systemFixture = systemFixture;
    }

    [Fact]
    public async void CreateRtEntity()
    {
        var systemContext = _systemFixture.GetSystemContext();
        if (await systemContext.IsSystemTenantExistingAsync())
        {
            await systemContext.DeleteSystemTenantAsync();
        }
        await systemContext.CreateSystemTenantAsync();

        var tenantRepository = await systemContext.CreateOrGetTenantRepositoryAsync();
       // tenantRepository.CreateTransientRtEntity<>()
    }
}