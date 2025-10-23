using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkRecordQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginCkQuery<CkId<CkRecordId>, CkRecord>(metricsContext, mongoDbRepositoryDataSource.CkRecords)
{
    protected override void AddPreFieldFilters(List<FilterDefinition<CkRecord>> filters)
    {
        filters.Add(Builders<CkRecord>.Filter.Eq(ckType => ckType.ModelState, ModelState.Available));
        base.AddPreFieldFilters(filters);
    }
}
