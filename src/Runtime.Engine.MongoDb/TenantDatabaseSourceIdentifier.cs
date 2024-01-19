using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public record TenantDatabaseSourceIdentifier(ICkMongoDbRepositoryDataSource MongoDbRepositoryDataSource);