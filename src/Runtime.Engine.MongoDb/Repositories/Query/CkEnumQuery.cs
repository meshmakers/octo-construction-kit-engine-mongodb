using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkEnumQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginCkQuery<CkId<CkEnumId>, CkEnum>(metricsContext, mongoDbRepositoryDataSource.CkEnums)
{
    protected override void AddPreFieldFilters(List<FilterDefinition<CkEnum>> filters)
    {
        filters.Add(Builders<CkEnum>.Filter.Eq(ckType => ckType.ModelState, ModelState.Available));
        base.AddPreFieldFilters(filters);
    }
}
