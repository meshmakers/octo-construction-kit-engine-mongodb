using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId InheritanceId { get; set; }

    CkTypeId OriginCkId { get; set; }
    CkTypeId TargetCkId { get; set; }
}