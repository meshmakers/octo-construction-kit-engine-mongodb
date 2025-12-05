using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkAssociationRoleQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginCkQuery<CkId<CkAssociationRoleId>, CkAssociationRole>(metricsContext, mongoDbRepositoryDataSource.CkAssociationRoles)
{
    protected override void AddPreFieldFilters(List<FilterDefinition<CkAssociationRole>> filters)
    {
        filters.Add(Builders<CkAssociationRole>.Filter.Eq(ckType => ckType.ModelState, ModelState.Available));
        base.AddPreFieldFilters(filters);
    }
}
