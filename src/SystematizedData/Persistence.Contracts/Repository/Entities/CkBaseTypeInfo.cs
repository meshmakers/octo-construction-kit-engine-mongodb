using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkBaseTypeInfo : ICkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public CkTypeId OriginCkId { get; set; }

    public CkTypeId TargetCkId { get; set; }

    public int BaseTypeDepthIndex { get; set; }
}