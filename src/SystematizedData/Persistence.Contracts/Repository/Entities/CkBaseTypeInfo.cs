using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkBaseTypeInfo 
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public CkId<CkTypeId> OriginCkTypeId { get; set; }

    public CkId<CkTypeId> TargetCkTypeId { get; set; }

    public int BaseTypeDepthIndex { get; set; }
}