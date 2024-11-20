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
    /// Gets or sets optional field filters to filter by on the version before storing runtime entity object.
    /// </summary>
    public ICollection<FieldFilter>? BeforeFieldFilters { get; set; }
    
    /// <summary>
    /// Gets or sets optional field filters to filter by on the current runtime entity object.
    /// </summary>
    public ICollection<FieldFilter>? FieldFilters { get; set; }
}