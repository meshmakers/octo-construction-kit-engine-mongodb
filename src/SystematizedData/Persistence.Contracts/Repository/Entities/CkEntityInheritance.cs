using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkEntityInheritance : ICkEntityInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public CkTypeId OriginCkId { get; set; } = null!;

    public CkTypeId TargetCkId { get; set; } = null!;
}
