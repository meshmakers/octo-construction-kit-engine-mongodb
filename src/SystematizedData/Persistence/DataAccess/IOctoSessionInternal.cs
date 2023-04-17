using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

internal interface IOctoSessionInternal : IOctoSession
{
    IClientSessionHandle SessionHandle { get; }
}
