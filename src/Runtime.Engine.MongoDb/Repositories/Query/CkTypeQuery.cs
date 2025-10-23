using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkTypeQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginCkQuery<CkId<CkTypeId>, CkType>(metricsContext, mongoDbRepositoryDataSource.CkTypes)
{
    protected override void AddPreFieldFilters(List<FilterDefinition<CkType>> filters)
    {
        filters.Add(Builders<CkType>.Filter.Eq(ckType => ckType.ModelState, ModelState.Available));
        base.AddPreFieldFilters(filters);
    }
}
