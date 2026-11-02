using System.Text;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Output-key rules of stream-data aggregation columns (AB#8168). The key of an aggregation column is
/// <c>{column}_{functionSuffix}</c>; Refinery Studio, MeshBoard widgets and persisted queries read these
/// names, so they are a wire contract.
/// <list type="bullet">
/// <item>A single <c>StateDuration</c> column on an attribute keeps the key <c>{column}_stateduration</c>.</item>
/// <item>Several <c>StateDuration</c> columns on the same attribute (compared case-insensitively, e.g.
/// state 0 and state 100) are told apart by the comparison value:
/// <c>{column}_stateduration_{qualifier}</c>, e.g. <c>position_stateduration_0</c> and
/// <c>position_stateduration_100</c>.</item>
/// <item>Qualifier = trimmed comparison value, lower-cased; letters and digits are kept, <c>-</c> becomes
/// <c>neg</c>, every other character becomes <c>_</c>, runs of <c>_</c> are collapsed and trimmed
/// (<c>-1.5</c> becomes <c>neg1_5</c>, <c>Low Power</c> becomes <c>low_power</c>). A value without a
/// letter or digit becomes <c>x</c>.</item>
/// </list>
/// Both the engine (row keys) and the GraphQL layer (wire mapping) call this class so the two sides
/// cannot drift apart.
/// </summary>
public static class StreamDataAggregationOutputNaming
{
    /// <summary>Function suffix of a StateDuration column that is the only one on its attribute.</summary>
    public const string StateDurationSuffix = "stateduration";

    /// <summary>
    /// Function suffix of the StateDuration column <paramref name="column"/> within
    /// <paramref name="allColumns"/>: <c>stateduration</c>, or <c>stateduration_{qualifier}</c> when
    /// another StateDuration column of the same attribute is present.
    /// </summary>
    public static string StateDurationFunctionSuffix(
        IReadOnlyList<AggregationColumn> allColumns, AggregationColumn column)
    {
        var shared = allColumns.Count(c =>
            c.Function == AggregationFunction.StateDuration
            && string.Equals(c.AttributePath, column.AttributePath, StringComparison.OrdinalIgnoreCase)) > 1;
        return shared
            ? FunctionSuffix(column.ComparisonValue)
            : StateDurationSuffix;
    }

    /// <summary>
    /// Function suffix for a StateDuration column that shares its attribute with another one:
    /// <c>stateduration_{qualifier}</c>. A null or blank value yields the plain suffix.
    /// </summary>
    public static string FunctionSuffix(string? comparisonValue) =>
        string.IsNullOrWhiteSpace(comparisonValue)
            ? StateDurationSuffix
            : $"{StateDurationSuffix}_{SanitizeQualifier(comparisonValue)}";

    /// <summary>The documented qualifier rule (see the class remarks).</summary>
    public static string SanitizeQualifier(string comparisonValue)
    {
        var sb = new StringBuilder();
        var lastUnderscore = true; // suppresses a leading '_'
        foreach (var ch in comparisonValue.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastUnderscore = false;
            }
            else if (ch == '-')
            {
                sb.Append("neg");
                lastUnderscore = false;
            }
            else if (!lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }

        var result = sb.ToString().TrimEnd('_');
        return result.Length == 0 ? "x" : result;
    }

    /// <summary>Function suffix of the output key for the functions other than StateDuration.</summary>
    private static string OutputFunctionSuffix(AggregationFunction function) => function switch
    {
        AggregationFunction.Average => "avg",
        AggregationFunction.Minimum => "min",
        AggregationFunction.Maximum => "max",
        AggregationFunction.TimeWeightedAverage => "twavg",
        _ => function.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Rejects aggregation columns whose output keys would collide: the same attribute and function
    /// twice, or StateDuration columns on one attribute whose comparison values reduce to the same
    /// qualifier. The message names the columns and what to change; it carries no internal detail.
    /// </summary>
    /// <exception cref="Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException">Two columns would share an output key.</exception>
    public static void ValidateDistinct(IReadOnlyList<AggregationColumn> columns, OctoObjectId archiveRtId)
    {
        var seen = new Dictionary<string, AggregationColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
        {
            // The key the query builders emit: physical column name + function suffix (so `a.b` and `ab`, or a
            // column literally named `foo_stateduration`, are compared as they will appear in the result).
            var suffix = col.Function == AggregationFunction.StateDuration
                ? StateDurationFunctionSuffix(columns, col)
                : OutputFunctionSuffix(col.Function);
            var key = $"{ColumnNameMapper.PathToColumnName(col.AttributePath)}_{suffix}";
            if (!seen.TryGetValue(key, out var first))
            {
                seen[key] = col;
                continue;
            }

            var message = col.Function == AggregationFunction.StateDuration
                ? $"StateDuration columns on '{col.AttributePath}' with the comparison values '{first.ComparisonValue}' and " +
                  $"'{col.ComparisonValue}' produce the same result column. Use a distinct comparison value for each StateDuration column."
                : $"The aggregation {col.Function} on '{col.AttributePath}' is requested more than once and would produce the same result column. " +
                  "Request each aggregation of an attribute only once.";
            throw new Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException(message, archiveRtId);
        }
    }
}
