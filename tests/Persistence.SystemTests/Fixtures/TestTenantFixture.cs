using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class TestTenantFixture : SystemFixture
{
    // ReSharper disable once MemberCanBeProtected.Global
    public TestTenantFixture()
    {

        Task.WaitAll(Task.Run(async () =>
        {
            var systemContext = GetSystemContext();
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var session = await systemContext.GetAdminSessionAsync();
                    session.StartTransaction();
                    
                    await systemContext.CreateChildTenantAsync(session, TestTenantId, TestTenantId);

                    await session.CommitTransactionAsync();

                    break;
                }
                catch (TenantException)
                {
                    Thread.Sleep(1000);
                    // do nothing here
                }
            }

        }));
    }

    public string TestTenantId => Options.TenantId;

}