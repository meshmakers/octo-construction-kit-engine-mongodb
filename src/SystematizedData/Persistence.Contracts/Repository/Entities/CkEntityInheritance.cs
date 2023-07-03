using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkEntityInheritance : ICkEntityInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public string OriginCkId { get; set; } = null!;

    public string TargetCkId { get; set; } = null!;
}
