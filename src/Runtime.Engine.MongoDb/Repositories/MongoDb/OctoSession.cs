using System.Diagnostics;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

[DebuggerDisplay("{" + nameof(ApplicationName) + "}")]
internal class OctoSession : IOctoSessionInternal
{
    internal OctoSession(IClientSessionHandle clientSessionHandle, string applicationName)
    {
        SessionHandle = clientSessionHandle;
        ApplicationName = applicationName;
    }

    public string ApplicationName { get; set; }

    public void Dispose()
    {
        SessionHandle.Dispose();
    }

    public void StartTransaction()
    {
        SessionHandle.StartTransaction();
    }

    public async Task CommitTransactionAsync()
    {
        await SessionHandle.CommitTransactionAsync();
    }

    public async Task AbortTransactionAsync()
    {
        await SessionHandle.AbortTransactionAsync();
    }

    public IClientSessionHandle SessionHandle { get; }
}