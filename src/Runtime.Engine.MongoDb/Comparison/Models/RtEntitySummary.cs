using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Summary information about a runtime entity
/// </summary>
public class RtEntitySummary
{
    /// <summary>
    ///     Entity runtime identifier (ObjectId)
    /// </summary>
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Construction Kit type identifier
    /// </summary>
    public CkId<CkTypeId> RtCkTypeId { get; set; } = null!;

    /// <summary>
    ///     Well-known name of the entity (if available)
    /// </summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>
    ///     Entity creation timestamp
    /// </summary>
    public DateTimeOffset? RtCreatedDate { get; set; }

    /// <summary>
    ///     Entity last modification timestamp
    /// </summary>
    public DateTimeOffset? RtLastModifiedDate { get; set; }

    /// <summary>
    ///     Key properties for identification (e.g., Name, Code, etc.)
    ///     Useful for displaying entity information in reports
    /// </summary>
    public Dictionary<string, object?> KeyProperties { get; set; } = new();
}
