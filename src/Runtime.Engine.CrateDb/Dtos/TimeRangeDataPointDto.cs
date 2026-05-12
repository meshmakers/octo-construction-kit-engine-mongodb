using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

/// <summary>
/// Counterpart of <see cref="DataPointDto"/> for time-range archive rows. Carries an explicit
/// half-open <c>[From, To)</c> window instead of a single <c>Timestamp</c>; the natural key on
/// the storage side is <c>(window_start, window_end, rtid, ckTypeId)</c>. Inherits the
/// dynamic-attributes machinery from <c>RtTypeWithAttributes</c> so user columns map the same
/// way as for raw archives.
/// </summary>
public class TimeRangeDataPointDto : RtTypeWithAttributes
{
    /// <summary>Constructs an empty time-range data point.</summary>
    public TimeRangeDataPointDto(Dictionary<string, object?> attributes) : base(attributes)
    {
    }

    /// <summary>Runtime id of the entity.</summary>
    public OctoObjectId? RtId { get; set; }

    /// <summary>CK type id of the entity (matches the archive's TargetCkTypeId).</summary>
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>Optional human-readable name.</summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>Inclusive window start (UTC).</summary>
    public DateTime From { get; set; }

    /// <summary>Exclusive window end (UTC).</summary>
    public DateTime To { get; set; }

    /// <inheritdoc />
    protected override string GetLocation() => "StreamData";
}
