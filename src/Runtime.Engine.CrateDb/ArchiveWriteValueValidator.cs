using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Checks the values of a time-range batch against the types of the archive's columns before anything is
/// written. A value of the wrong type used to reach CrateDB, which refused the whole statement, so the caller
/// saw an unexplained server error (REST: HTTP 500). Every mismatch is collected and the batch is refused once
/// with an <see cref="ArgumentException" /> naming the point, the column and the expected type — the same
/// "pre-validate, then commit, never partial" rule as the window check of <c>InsertTimeRangeAsync</c>.
/// </summary>
/// <remarks>
/// An enum column takes the key or the member name (case-insensitive), the same rule as the pipeline write
/// path (<c>RtPathEvaluator.SetValueByPath</c>); a name is stored as its key. Only scalar leaves of the
/// archive's ingested columns are checked; strings, records, arrays and geo points pass through unchanged.
/// </remarks>
internal static class ArchiveWriteValueValidator
{
    /// <summary>At most this many mismatches are listed in the message; the count names the rest.</summary>
    private const int MaxReportedMismatches = 10;

    /// <summary>At most this many enum values are listed as the valid choices.</summary>
    private const int MaxListedEnumValues = 20;

    /// <summary>
    /// Returns the points with enum names replaced by their keys, or throws when any value does not fit its
    /// column. Points of another CK type are left as they are (the repository skips them).
    /// </summary>
    /// <param name="ckCache">The CK cache of the tenant.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="snapshot">The archive the batch is written to.</param>
    /// <param name="points">The batch in the caller's order; indexes in the message refer to it.</param>
    /// <param name="flatten">Expands record values into dotted column paths, as the insert path does.</param>
    public static IReadOnlyList<TimeRangeStreamDataPoint> Normalize(ICkCacheService ckCache, string tenantId,
        ArchiveSnapshot snapshot, IReadOnlyList<TimeRangeStreamDataPoint> points,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> flatten)
    {
        var columns = ResolveColumns(ckCache, tenantId, snapshot);
        if (columns.Count == 0)
        {
            return points;
        }

        var mismatches = new List<string>();
        var result = new List<TimeRangeStreamDataPoint>(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (point.CkTypeId != snapshot.TargetCkTypeId)
            {
                result.Add(point);
                continue;
            }

            var flattened = flatten(point.Attributes);
            Dictionary<string, object?>? normalized = null;
            foreach (var (key, value) in flattened)
            {
                if (value is null || !columns.TryGetValue(ColumnNameMapper.PathToColumnName(key), out var column))
                {
                    continue;
                }

                if (!TryConvert(ckCache, tenantId, column.Attribute, value, out var converted, out var expected))
                {
                    mismatches.Add($"point {index}: column '{column.Path}' expects {expected}, got {Describe(value)}");
                    continue;
                }

                if (!Equals(converted, value))
                {
                    normalized ??= new Dictionary<string, object?>(flattened, StringComparer.Ordinal);
                    normalized[key] = converted;
                }
            }

            result.Add(normalized is null ? point : WithAttributes(point, normalized));
        }

        if (mismatches.Count > 0)
        {
            var listed = string.Join("; ", mismatches.Take(MaxReportedMismatches));
            var more = mismatches.Count > MaxReportedMismatches
                ? $" (and {mismatches.Count - MaxReportedMismatches} more)"
                : string.Empty;
            throw new ArgumentException(
                $"Archive '{snapshot.RtId}': {mismatches.Count} value(s) do not fit their column, nothing was written: {listed}{more}.",
                nameof(points));
        }

        return result;
    }

    private sealed record ColumnInfo(string Path, CkTypeAttributeGraph Attribute);

    /// <summary>The scalar leaf attribute of every ingested column, keyed by its physical column name.</summary>
    private static Dictionary<string, ColumnInfo> ResolveColumns(ICkCacheService ckCache, string tenantId,
        ArchiveSnapshot snapshot)
    {
        var columns = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
        CkTypeGraph? ckType = null;
        foreach (var column in snapshot.Columns)
        {
            if (column.IsComputed || string.IsNullOrWhiteSpace(column.Path))
            {
                continue;
            }

            ckType ??= ckCache.GetRtCkType(tenantId, snapshot.TargetCkTypeId);
            if (ResolveLeaf(ckCache, tenantId, ckType, column.Path) is { } leaf)
            {
                columns[ColumnNameMapper.PathToColumnName(column.Path)] = new ColumnInfo(column.Path, leaf);
            }
        }

        return columns;
    }

    /// <summary>
    /// Walks a dotted column path through records to its leaf attribute. <c>null</c> when the path does not
    /// resolve: activation would have refused such a column, so there is nothing to check against.
    /// </summary>
    private static CkTypeAttributeGraph? ResolveLeaf(ICkCacheService ckCache, string tenantId, CkTypeGraph ckType,
        string path)
    {
        var segments = path.Split('.');
        if (!ckType.AllAttributesByName.TryGetValue(segments[0], out var attribute))
        {
            return null;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (attribute.ValueCkRecordId is null ||
                !ckCache.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var record) || record is null ||
                !record.AllAttributesByName.TryGetValue(segments[i], out var next))
            {
                return null;
            }

            attribute = next;
        }

        return attribute;
    }

    private static bool TryConvert(ICkCacheService ckCache, string tenantId, CkTypeAttributeGraph attribute,
        object value, out object? converted, out string expected)
    {
        converted = value;
        switch (attribute.ValueType)
        {
            case AttributeValueTypesDto.Enum:
                return TryConvertEnum(ckCache, tenantId, attribute, value, out converted, out expected);

            case AttributeValueTypesDto.Integer:
            case AttributeValueTypesDto.Integer64:
            case AttributeValueTypesDto.TimeSpan:
                expected = "an integer";
                return value is int or long or short or byte or sbyte or ushort or uint ||
                       value is double d && Math.Abs(d % 1) < double.Epsilon ||
                       value is decimal m && decimal.Truncate(m) == m;

            case AttributeValueTypesDto.Double:
                expected = "a number";
                return value is double or float or decimal or int or long or short or byte or sbyte or ushort or uint;

            case AttributeValueTypesDto.Boolean:
                expected = "true or false";
                return value is bool;

            case AttributeValueTypesDto.DateTime:
            case AttributeValueTypesDto.DateTimeOffset:
                expected = "a date and time (ISO 8601)";
                return value is DateTime or DateTimeOffset;

            default:
                expected = string.Empty;
                return true;
        }
    }

    private static bool TryConvertEnum(ICkCacheService ckCache, string tenantId, CkTypeAttributeGraph attribute,
        object value, out object? converted, out string expected)
    {
        converted = value;
        if (attribute.ValueCkEnumId is null ||
            !ckCache.TryGetCkEnum(tenantId, attribute.ValueCkEnumId, out var ckEnum) || ckEnum is null)
        {
            expected = "an enum key";
            return value is int or long;
        }

        var values = ckEnum.Values;
        var choices = string.Join(", ", values.Take(MaxListedEnumValues).Select(v => $"{v.Name} ({v.Key})"));
        if (values.Count > MaxListedEnumValues)
        {
            choices += ", ...";
        }

        expected = $"a value of enum '{attribute.ValueCkEnumId}': {choices}";

        if (value is string text)
        {
            var match = values.FirstOrDefault(v => string.Equals(v.Name, text, StringComparison.OrdinalIgnoreCase)) ??
                        values.FirstOrDefault(v => string.Equals(v.Key.ToString(CultureInfo.InvariantCulture), text.Trim(),
                            StringComparison.Ordinal));
            if (match is null)
            {
                return false;
            }

            converted = match.Key;
            return true;
        }

        if (value is int or long or short or byte)
        {
            var key = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            // A flags enum stores combinations that are not listed one by one; only the type is checked there.
            if (ckEnum.UseFlags || values.Any(v => v.Key == key))
            {
                converted = (int)key;
                return true;
            }
        }

        return false;
    }

    private static string Describe(object value) => value switch
    {
        string s => $"text '{(s.Length > 40 ? s[..40] + "..." : s)}'",
        bool b => b ? "true" : "false",
        DateTime or DateTimeOffset => $"date '{Convert.ToString(value, CultureInfo.InvariantCulture)}'",
        _ => $"{value.GetType().Name} '{Convert.ToString(value, CultureInfo.InvariantCulture)}'"
    };

    private static TimeRangeStreamDataPoint WithAttributes(TimeRangeStreamDataPoint point,
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
}
