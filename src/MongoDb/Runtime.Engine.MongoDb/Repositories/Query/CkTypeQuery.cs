using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

public class CkTypeQuery : SingleOriginQuery<CkId<CkTypeId>, CkType>
{
    public CkTypeQuery(IMongoDbRepositoryDataSource mongoDbRepositoryDataSource) : base(mongoDbRepositoryDataSource.CkTypes)
    {
    }
}