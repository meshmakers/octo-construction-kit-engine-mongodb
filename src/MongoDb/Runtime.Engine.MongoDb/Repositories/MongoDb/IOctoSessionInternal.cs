using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal interface IOctoSessionInternal : IOctoSystemSession
{
    IClientSessionHandle SessionHandle { get; }
}