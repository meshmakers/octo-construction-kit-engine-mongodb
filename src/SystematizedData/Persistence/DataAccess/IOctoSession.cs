using System;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public interface IOctoSession : IDisposable
{
    void StartTransaction();

    Task CommitTransactionAsync();

    Task AbortTransactionAsync();
}
