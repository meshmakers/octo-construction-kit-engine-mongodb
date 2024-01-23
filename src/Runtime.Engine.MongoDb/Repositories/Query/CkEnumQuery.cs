using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkEnumQuery : SingleOriginQuery<CkId<CkEnumId>, CkEnum>
{
    public CkEnumQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
        : base(metricsContext, mongoDbRepositoryDataSource.CkEnums, new FieldFilterResolver<CkEnum>())
    {
    }
}