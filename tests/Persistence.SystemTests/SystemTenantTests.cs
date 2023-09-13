using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class SystemTenantTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;
    private readonly ITestOutputHelper _testOutputHelper;

    public SystemTenantTests(SystemFixture systemFixture, ITestOutputHelper testOutputHelper)
    {
        _systemFixture = systemFixture;
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async void IsSystemTenantExisting()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var result = await systemContext.IsSystemTenantExistingAsync();
        Assert.True(result);
    }
    
    [Fact]
    public async void CreateChildTenantAndDeleteAsync()
    {
        var systemContext = _systemFixture.GetSystemContext();
        using var session = await systemContext.GetSystemSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, "TestTenant", "TestTenant");

        await session.CommitTransactionAsync();

        using var session2 = await systemContext.GetSystemSessionAsync();
        session2.StartTransaction();
        var r = await systemContext.GetChildTenantAsync(session2, "TestTenant");
        await session2.CommitTransactionAsync();

        Assert.Equal("testtenant", r.TenantId);
        Assert.Equal("testtenant", r.DatabaseName);

        using var session3 = await systemContext.GetSystemSessionAsync();
        session3.StartTransaction();

        await systemContext.DropChildTenantAsync(session3, "TestTenant");

        await session3.CommitTransactionAsync();

        using var session4 = await systemContext.GetSystemSessionAsync();
        session4.StartTransaction();
        var r2 = await systemContext.IsChildTenantExistingAsync(session4, "TestTenant");
        await session4.CommitTransactionAsync();

        Assert.False(r2);
    }

    [Fact]
    public async void CreateIndirectTenantAndDeleteAsync()
    {
        var systemContext = _systemFixture.GetSystemContext();
        using var session = await systemContext.GetSystemSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, "TestTenant", "TestTenant");

        await session.CommitTransactionAsync();

        using var session2 = await systemContext.GetSystemSessionAsync();
        session2.StartTransaction();
        var testTenantContext = await systemContext.GetChildTenantContextAsync("TestTenant");

        await testTenantContext.CreateChildTenantAsync(session2, "TestTenant2", "TestTenant2");

        await session2.CommitTransactionAsync();


        using var session3 = await testTenantContext.GetSystemSessionAsync();
        session3.StartTransaction();
        var r = await testTenantContext.GetChildTenantAsync(session3, "TestTenant2");
        await session3.CommitTransactionAsync();

        Assert.Equal("testtenant2", r.TenantId);
        Assert.Equal("testtenant2", r.DatabaseName);


        using var session4 = await testTenantContext.GetSystemSessionAsync();
        session4.StartTransaction();
        await testTenantContext.DropChildTenantAsync(session4, "TestTenant2");
        await session4.CommitTransactionAsync();

        using var session5 = await testTenantContext.GetSystemSessionAsync();
        session5.StartTransaction();
        await systemContext.DropChildTenantAsync(session5, "TestTenant");
        await session5.CommitTransactionAsync();
    }
}