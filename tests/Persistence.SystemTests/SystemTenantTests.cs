using System;
using Xunit;
using Xunit.Abstractions;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

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
        using var session = await systemContext.StartSystemSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, "TestTenant", "TestTenant");
        
        await session.CommitTransactionAsync();
         
        using var session2 = await systemContext.StartSystemSessionAsync();
        session2.StartTransaction();
        var r = await systemContext.GetChildTenantAsync(session2, "TestTenant");
        await session2.CommitTransactionAsync();
        
        Assert.Equal("testtenant", r.TenantId);
        Assert.Equal("testtenant", r.DatabaseName);
        
        using var session3 = await systemContext.StartSystemSessionAsync();
        session3.StartTransaction();
        
        await systemContext.DropChildTenantAsync(session3, "TestTenant");
        
        await session3.CommitTransactionAsync();
        
        using var session4 = await systemContext.StartSystemSessionAsync();
        session4.StartTransaction();
        var r2 = await systemContext.IsChildTenantExistingAsync(session4, "TestTenant");
        await session4.CommitTransactionAsync();
        
        Assert.False(r2);
    }
}