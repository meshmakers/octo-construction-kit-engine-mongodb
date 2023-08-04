using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId InheritanceId { get; set; }

    CkId<CkTypeId> OriginCkId { get; set; }
    CkId<CkTypeId> TargetCkId { get; set; }
}