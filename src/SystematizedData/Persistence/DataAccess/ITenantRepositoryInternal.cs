using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

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
