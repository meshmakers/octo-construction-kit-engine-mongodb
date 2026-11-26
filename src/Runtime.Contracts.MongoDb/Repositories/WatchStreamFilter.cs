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

    /// <summary>
    ///     Opt-in for delete events. <c>false</c> (default): no delete event is delivered, whatever
    ///     <see cref="UpdateTypes" /> says - the behaviour of every subscription before this setting existed. <c>true</c>
    ///     with <see cref="UpdateTypes" /> including <see cref="Repositories.UpdateTypes.Delete" /> (or
    ///     <see cref="Repositories.UpdateTypes.Undefined" />): deletes of the watched type set are delivered with
    ///     <see cref="IUpdateInfo{T}.DocumentRtId" /> set and <see cref="IUpdateInfo{T}.Document" /> <c>null</c>.
    ///     A delete event carries no document, so the engine matches it on the pre-image when the collection root has
    ///     <c>EnableChangeStreamPreAndPostImages</c> (exact; the field filters and before-field filters apply to the
    ///     pre-image), otherwise on the document key alone - which is only exact for a subscription on the collection
    ///     root itself without field filters. Any other combination is refused when the subscription starts.
    /// </summary>
    public bool IncludeDeleteEvents { get; set; }
}
