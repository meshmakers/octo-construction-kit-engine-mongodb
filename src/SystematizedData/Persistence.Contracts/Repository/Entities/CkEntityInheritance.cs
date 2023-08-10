using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkEntityInheritance 
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public CkId<CkTypeId> OriginCkTypeId { get; set; }

    public CkId<CkTypeId> TargetCkTypeId { get; set; }
}
