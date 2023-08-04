using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkBaseTypeInfo : ICkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public CkId<CkTypeId> OriginCkId { get; set; }

    public CkId<CkTypeId> TargetCkId { get; set; }

    public int BaseTypeDepthIndex { get; set; }
}