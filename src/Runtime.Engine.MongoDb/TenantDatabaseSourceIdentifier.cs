using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

public record TenantDatabaseSourceIdentifier(IOctoSession? Session, ICkMongoDbRepositoryDataSource MongoDbRepositoryDataSource);
