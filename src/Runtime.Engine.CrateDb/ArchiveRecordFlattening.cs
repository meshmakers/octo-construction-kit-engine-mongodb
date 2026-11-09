using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Expands the record values of the points written to a raw or time-range archive into one dotted-path entry per
/// record member (AB#8666). The archive's columns address record members by their dotted path
/// (<c>NamePlate.SerialNumber</c>), while a pipeline delivers the entity's attributes as they are: the record under its
/// attribute name (<c>NamePlate</c> → <see cref="RtRecord" />). Both insert paths run their points through here once,
/// before the value check, the ranking-column check, the computed columns and the row mapping, so every later step sees
/// the same keys.
/// </summary>
/// <remarks>
/// <para>
/// A member with a value is written, a member set to <c>null</c> clears its column, and a member the record does not
/// contain is not named, so its column keeps the stored value (AB#8622 partial delivery). Nested records are expanded
/// recursively; a record without members names nothing.
/// </para>
/// <para>
/// A point without record values is returned as it is (same instance): the asset repository's REST <c>insert</c> and
/// <c>insertTimeRange</c> already send dotted column paths, so they pass through unchanged and nothing is flattened
/// twice. A record value under a key that is itself no record attribute of the archive simply names columns that do not
/// exist and is ignored like any other unknown key.
/// </para>
/// </remarks>
internal static class ArchiveRecordFlattening
{
    /// <summary>The raw points with their record values expanded; the list itself when no point carries a record.</summary>
    public static IReadOnlyList<StreamDataPoint> Flatten(IReadOnlyList<StreamDataPoint> points)
        => FlattenCore(points, p => p.Attributes, WithAttributes);

    /// <summary>The time-range points with their record values expanded; the list itself when no point carries a record.</summary>
    public static IReadOnlyList<TimeRangeStreamDataPoint> Flatten(IReadOnlyList<TimeRangeStreamDataPoint> points)
        => FlattenCore(points, p => p.Attributes, WithAttributes);

    /// <summary>
    /// The attributes with every <see cref="RtRecord" /> value replaced by one dotted-path entry per member
    /// (recursively); the dictionary itself when it holds no record value.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Flatten(IReadOnlyDictionary<string, object?> attributes)
    {
        if (!attributes.Values.Any(v => v is RtRecord))
        {
            return attributes;
        }

        var result = new Dictionary<string, object?>(attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            FlattenInto(result, key, value);
        }

        return result;
    }

    private static void FlattenInto(Dictionary<string, object?> result, string path, object? value)
    {
        if (value is RtRecord record)
        {
            foreach (var (member, memberValue) in record.Attributes)
            {
                FlattenInto(result, $"{path}.{member}", memberValue);
            }

            return;
        }

        result[path] = value;
    }

    private static IReadOnlyList<TPoint> FlattenCore<TPoint>(IReadOnlyList<TPoint> points,
        Func<TPoint, IReadOnlyDictionary<string, object?>> attributesOf,
        Func<TPoint, IReadOnlyDictionary<string, object?>, TPoint> withAttributes)
    {
        List<TPoint>? result = null;
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var attributes = attributesOf(point);
            var flattened = Flatten(attributes);
            if (result is null && ReferenceEquals(flattened, attributes))
            {
                continue;
            }

            result ??= new List<TPoint>(points.Take(index));
            result.Add(ReferenceEquals(flattened, attributes) ? point : withAttributes(point, flattened));
        }

        return result ?? points;
    }

    /// <summary>A copy of <paramref name="point" /> with other attributes; every other property is kept.</summary>
    internal static TimeRangeStreamDataPoint WithAttributes(TimeRangeStreamDataPoint point,
        IReadOnlyDictionary<string, object?> attributes) => new()
    {
        RtId = point.RtId,
        CkTypeId = point.CkTypeId,
        From = point.From,
        To = point.To,
        RtWellKnownName = point.RtWellKnownName,
        Attributes = attributes,
        Origin = point.Origin,
        ManualWrittenBy = point.ManualWrittenBy,
        ManualReason = point.ManualReason
    };

    /// <summary>A copy of <paramref name="point" /> with other attributes; every other property is kept.</summary>
    internal static StreamDataPoint WithAttributes(StreamDataPoint point,
        IReadOnlyDictionary<string, object?> attributes) => new()
    {
        RtId = point.RtId,
        CkTypeId = point.CkTypeId,
        Timestamp = point.Timestamp,
        RtWellKnownName = point.RtWellKnownName,
        RtCreationDateTime = point.RtCreationDateTime,
        RtChangedDateTime = point.RtChangedDateTime,
        Attributes = attributes,
        Origin = point.Origin,
        ManualWrittenBy = point.ManualWrittenBy,
        ManualReason = point.ManualReason
    };
}
