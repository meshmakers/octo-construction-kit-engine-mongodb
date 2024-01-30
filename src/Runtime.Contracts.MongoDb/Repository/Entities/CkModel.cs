using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

[DebuggerDisplay("{" + nameof(Id) + "}")]
public class CkModel
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkModel()
    {
        Dependencies = Array.Empty<CkModelId>();
    }

    /// <summary>
    ///     Defines the id of the construction kit model
    /// </summary>
    public CkModelId Id { get; init; } = null!;

    /// <summary>
    /// Defines the name of construction kit model without version
    /// </summary>
    public string ModelId { get; init; } = null!;
    
    /// <summary>
    ///     Defines the state of the construction kit model
    /// </summary>
    public ModelState ModelState { get; init; }

    /// <summary>
    ///     Defines the dependencies of the construction kit
    /// </summary>
    public CkModelId[]? Dependencies { get; init; }
}