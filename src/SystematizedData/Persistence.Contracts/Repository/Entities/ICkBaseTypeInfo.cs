using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId InheritanceId { get; set; }

    ScopeIds ScopeId { get; set; }
    CkTypeId OriginCkId { get; set; }
    CkTypeId TargetCkId { get; set; }
    int BaseTypeDepthIndex { get; set; }
}