using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal interface IOctoSessionInternal : IOctoSystemSession
{
    IClientSessionHandle SessionHandle { get; }
}
