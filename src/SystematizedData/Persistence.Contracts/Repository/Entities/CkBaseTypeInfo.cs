using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkBaseTypeInfo : ICkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public string OriginCkId { get; set; }

    public string TargetCkId { get; set; }

    public int BaseTypeDepthIndex { get; set; }
}