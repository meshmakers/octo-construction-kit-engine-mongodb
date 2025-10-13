using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Information about a Construction Kit model
/// </summary>
public class CkModelInfo
{
    /// <summary>
    ///     Complete model identifier including version
    /// </summary>
    public CkModelId CkModelId { get; set; } = null!;

    /// <summary>
    ///     Model identifier without version
    /// </summary>
    public string ModelId { get; set; } = null!;

    /// <summary>
    ///     Current state of the model
    /// </summary>
    public ModelState ModelState { get; set; }

    /// <summary>
    ///     List of model dependencies
    /// </summary>
    public List<CkModelId> Dependencies { get; set; } = new();

    /// <summary>
    ///     Model description (if available)
    /// </summary>
    public string? Description { get; set; }
}
