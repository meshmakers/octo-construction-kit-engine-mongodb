using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal interface IOctoSessionInternal : IOctoSession
{
    IClientSessionHandle SessionHandle { get; }
}
