using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId InheritanceId { get; set; }

    ScopeIds ScopeId { get; set; }
    CkId<CkTypeId> OriginCkId { get; set; }
    CkId<CkTypeId> TargetCkId { get; set; }
    int BaseTypeDepthIndex { get; set; }
}