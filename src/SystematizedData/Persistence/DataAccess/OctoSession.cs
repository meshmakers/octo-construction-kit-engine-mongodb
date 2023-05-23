using System.Threading.Tasks;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class OctoSession : IOctoSessionInternal
{
    internal OctoSession(IClientSessionHandle clientSessionHandle)
    {
        SessionHandle = clientSessionHandle;
    }

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
