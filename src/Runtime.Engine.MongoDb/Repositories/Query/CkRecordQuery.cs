using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkRecordQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginQuery<CkId<CkRecordId>, CkRecord>(metricsContext, mongoDbRepositoryDataSource.CkRecords,
        new FieldFilterResolver<CkRecord>());