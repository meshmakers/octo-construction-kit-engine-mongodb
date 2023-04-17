using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal interface ITenantRepositoryInternal : ITenantRepository
{
    Task<CurrentMultiplicity> GetCurrentRtAssociationMultiplicityAsync(IOctoSession session, RtEntityId rtEntityId,
        string roleId, GraphDirections graphDirections);

    Task InsertRtEntitiesAsync(IOctoSession session, IEnumerable<RtEntity> rtEntityList,
        bool disableAutoIncrement = false);

    Task<AggregatedBulkImportResult>
        BulkInsertRtEntitiesAsync(IOctoSession session, IEnumerable<RtEntity> rtEntityList);

    Task InsertRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations);
    Task<BulkImportResult> BulkRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations);
}
