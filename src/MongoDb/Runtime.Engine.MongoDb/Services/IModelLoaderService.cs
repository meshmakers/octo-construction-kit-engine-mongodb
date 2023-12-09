using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public interface IModelLoaderService
{
    Task LoadAsync(string tenantId, IOctoSession session, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource);
}