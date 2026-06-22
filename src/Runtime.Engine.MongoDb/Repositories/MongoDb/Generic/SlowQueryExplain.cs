namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Result of asynchronously running <c>db.runCommand({explain: ..., verbosity: "queryPlanner"})</c>
/// against the originating database for a captured slow query. Attached to
/// <see cref="SlowQueryEntry"/> at read time so the Refinery Studio Diagnostics surface can
/// show whether the query is a <c>COLLSCAN</c> and which indexes (if any) were considered.
/// </summary>
/// <param name="CapturedAt">UTC instant when the explain finished (or failed).</param>
/// <param name="Status">Outcome marker — see <see cref="SlowQueryExplainStatus"/>.</param>
/// <param name="WinningStage">
/// Top-level <c>queryPlanner.winningPlan.stage</c> value (e.g. <c>COLLSCAN</c>, <c>IXSCAN</c>,
/// <c>FETCH</c>, <c>SORT</c>, <c>AGGREGATE</c>). Empty string when <see cref="Status"/> is not
/// <c>success</c>.
/// </param>
/// <param name="HasCollScan">
/// <c>true</c> if any node in the winning-plan tree (top-level or nested via
/// <c>inputStage</c>/<c>inputStages</c>) is a <c>COLLSCAN</c>. The primary signal Stage 2B is
/// shipped for.
/// </param>
/// <param name="IndexNames">
/// Every <c>IXSCAN.indexName</c> encountered while walking the winning plan, in document
/// order. Empty for full collection scans.
/// </param>
/// <param name="RawExplainPreview">
/// Truncated JSON of <c>queryPlanner</c> capped at <c>SlowQueryExplainPreviewBytes</c> UTF-8
/// bytes — for power users / deeper drill-down in the Studio surface. <c>null</c> on failure /
/// unsupported / when the preview budget is zero.
/// </param>
/// <param name="ErrorMessage">
/// Short failure cause when <see cref="Status"/> is <c>failed</c> (e.g. <c>"timeout"</c>,
/// <c>"command not supported"</c>, exception type name). <c>null</c> otherwise.
/// </param>
/// <param name="IndexSuggestion">
/// MongoDB index suggestion emitted when <see cref="HasCollScan"/> is <c>true</c> and the
/// suggester could extract at least one filter field (Stage 2C / AB#4220). <c>null</c>
/// otherwise — e.g. for IXSCAN plans, aggregates without a leading <c>$match</c>, or empty
/// filters. Carries a ready-to-run <c>db.&lt;coll&gt;.createIndex(...)</c> shell command.
/// </param>
public sealed record SlowQueryExplain(
    DateTimeOffset CapturedAt,
    SlowQueryExplainStatus Status,
    string WinningStage,
    bool HasCollScan,
    IReadOnlyList<string> IndexNames,
    string? RawExplainPreview,
    string? ErrorMessage,
    SlowQueryIndexSuggestion? IndexSuggestion = null);

/// <summary>
/// Outcome of an explain capture attempt.
/// </summary>
public enum SlowQueryExplainStatus
{
    /// <summary>
    /// The driver returned an explain document and the parser extracted a winning plan.
    /// </summary>
    Success,

    /// <summary>
    /// The command type does not support <c>explain</c> (e.g. <c>insert</c>, write concerns
    /// other than the explainable set). No round-trip was attempted.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The driver call failed (timeout, transient error, malformed BSON). The cache entry
    /// is kept so the cooldown still applies and we do not retry-storm on a broken shape.
    /// </summary>
    Failed
}
