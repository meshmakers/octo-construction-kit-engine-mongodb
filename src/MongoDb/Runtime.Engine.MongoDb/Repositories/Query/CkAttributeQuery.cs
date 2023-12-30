using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

public class CkAttributeQuery : SingleOriginQuery<CkId<CkAttributeId>, CkAttribute>
{
    public CkAttributeQuery(IMongoDbRepositoryDataSource mongoDbRepositoryDataSource) : base(mongoDbRepositoryDataSource.CkAttributes)
    {
    }
}