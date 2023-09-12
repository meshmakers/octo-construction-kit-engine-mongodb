using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class CkTypeInheritance 
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId InheritanceId { get; set; }
    
    /// <summary>
    ///     Gets or sets the construction kit model id 
    /// </summary>
    public CkModelId CkModelId { get; set; }

    public CkId<CkTypeId> BaseCkTypeId { get; set; }

    public CkId<CkTypeId> InheritorCkTypeId { get; set; }
}
