using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

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