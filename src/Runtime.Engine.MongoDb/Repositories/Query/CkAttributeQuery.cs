using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkAttributeQuery(
    IMetricsContext metricsContext,
    IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginCkQuery<CkId<CkAttributeId>, CkAttribute>(metricsContext, mongoDbRepositoryDataSource.CkAttributes)
{
    protected override void AddPreFieldFilters(List<FilterDefinition<CkAttribute>> filters)
    {
        filters.Add(Builders<CkAttribute>.Filter.Eq(ckType => ckType.ModelState, ModelState.Available));
        base.AddPreFieldFilters(filters);
    }
}
