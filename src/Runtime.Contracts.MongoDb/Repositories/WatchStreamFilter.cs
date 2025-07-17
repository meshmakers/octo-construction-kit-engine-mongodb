using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
/// Defines a filter for the update stream.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class WatchStreamFilter
{
    /// <summary>
    /// Gets or sets the update types.
    /// </summary>
    public UpdateTypes UpdateTypes { get; set; }

    /// <summary>
    /// Gets or sets the runtime identifier of an object to filter by (optional).
    /// </summary>
    public OctoObjectId? RtId { get; set; }
    
    /// <summary>
    /// Gets or sets optional field filter criteria to filter by on the version before storing a runtime entity object.
    /// </summary>
    public FieldFilterCriteria? BeforeFieldFilterCriteria { get; set; }
    
    /// <summary>
    /// Gets or sets optional field filter criteria to filter by on the version after storing a runtime entity object.
    /// </summary>
    public FieldFilterCriteria? FieldFilterCriteria { get; set; }
}
