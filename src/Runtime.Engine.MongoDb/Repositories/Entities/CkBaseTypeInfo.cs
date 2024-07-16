using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

public class CkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    public CkId<CkTypeId> OriginCkTypeId { get; set; } = null!;

    public CkId<CkTypeId> TargetCkTypeId { get; set; } = null!;

    public int BaseTypeDepthIndex { get; set; }
}