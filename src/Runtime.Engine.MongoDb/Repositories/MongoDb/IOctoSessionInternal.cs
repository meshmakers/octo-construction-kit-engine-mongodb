using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal interface IOctoSessionInternal
{
    IClientSessionHandle SessionHandle { get; }
}