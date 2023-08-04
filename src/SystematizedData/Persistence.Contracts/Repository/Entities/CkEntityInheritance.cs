using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkEntityInheritance : ICkEntityInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public CkId<CkTypeId> OriginCkId { get; set; }

    public CkId<CkTypeId> TargetCkId { get; set; }
}
