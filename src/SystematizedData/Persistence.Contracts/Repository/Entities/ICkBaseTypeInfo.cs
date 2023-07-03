using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId InheritanceId { get; set; }

    ScopeIds ScopeId { get; set; }
    string OriginCkId { get; set; }
    string TargetCkId { get; set; }
    int BaseTypeDepthIndex { get; set; }
}