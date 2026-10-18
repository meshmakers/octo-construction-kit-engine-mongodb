using System.Text.RegularExpressions;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// CrateDB records every finished statement with its full text in <c>sys.jobs_log</c>, and for a <c>COPY</c>
/// that text carries the storage credential. The cluster setting <c>stats.jobs_log_filter</c> keeps
/// statements out of that log; the staging copy refuses to start while no filter excludes <c>COPY</c>
/// (concept §5, decision 7).
/// </summary>
internal static class JobLogFilterGuard
{
    /// <summary>The setting, as it appears in <c>SET GLOBAL</c>.</summary>
    public const string SettingName = "stats.jobs_log_filter";

    /// <summary>The value the infrastructure sets: record every statement except <c>COPY</c>.</summary>
    public const string RecommendedFilter = "stmt NOT ILIKE 'copy %'";

    /// <summary>Reads the cluster's current filter expression.</summary>
    public static readonly CopyStatement ReadSetting = CopyStatement.Plain(
        "SELECT settings['stats']['jobs_log_filter'] AS jobs_log_filter FROM sys.cluster", returnsRows: true);

    // Whole-expression shape accepted as "COPY is excluded": one predicate, or a conjunction that contains
    // it. Anything with an OR could let a COPY through, and arbitrary expressions are not evaluated.
    private static readonly Regex ExcludesCopy = new(
        @"\bstmt\s+not\s+i?like\s+'\s*copy\s*%'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HasOr = new(@"\bor\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when the filter expression keeps <c>COPY</c> statements out of <c>sys.jobs_log</c>.</summary>
    public static bool IsFiltered(string? filter)
        => !string.IsNullOrWhiteSpace(filter) && ExcludesCopy.IsMatch(filter) && !HasOr.IsMatch(filter);

    /// <summary>Reads the setting and throws <see cref="ArchiveCopyRefusedException"/> when it does not exclude <c>COPY</c>.</summary>
    public static async Task EnsureFilteredAsync(
        ICrateCopyClient client, string tenantId, ArchiveCopyOptions options, Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId archiveRtId,
        CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, ReadSetting, options.CommandTimeout, cancellationToken);
        var filter = result.Rows.Count > 0 && result.Rows[0].TryGetValue("jobs_log_filter", out var value)
            ? value as string
            : null;

        if (!IsFiltered(filter))
        {
            throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.JobLogNotFiltered,
                $"The staging copy needs the cluster setting {SettingName} to keep COPY statements out of sys.jobs_log, " +
                $"because their text carries the storage credential. It is '{filter ?? "(not set)"}'. " +
                $"Set it, e.g. SET GLOBAL PERSISTENT \"{SettingName}\" = '{RecommendedFilter.Replace("'", "''", StringComparison.Ordinal)}'.",
                archiveRtId);
        }
    }
}
