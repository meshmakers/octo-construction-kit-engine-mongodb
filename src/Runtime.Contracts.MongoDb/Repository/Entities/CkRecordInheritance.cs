using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

public class CkRecordInheritance
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }

    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    public CkId<CkRecordId> BaseCkRecordId { get; set; } = null!;

    public CkId<CkRecordId> InheritorCkRecordId { get; set; } = null!;
}