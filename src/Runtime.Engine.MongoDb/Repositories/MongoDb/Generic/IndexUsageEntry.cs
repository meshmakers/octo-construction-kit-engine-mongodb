namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// One row of the Performance Advisor's index-usage surface (Stage 3 / AB#4224). Aggregates
/// MongoDB's <c>$indexStats</c> output across replica-set hosts so the operator sees a single
/// usage figure per index, paired with a paste-ready drop command when the index isn't
/// pulling its weight.
/// </summary>
/// <remarks>
/// The complementary signal to Stages 2C/2D: those stages tell the operator which indexes
/// to ADD when a slow query needs them; this entry tells the operator which indexes are
/// candidates for REMOVAL because they haven't been used. Together they close the
/// add-and-remove loop.
/// </remarks>
/// <param name="CollectionName">Tenant collection the index sits on (e.g. <c>rt_entities</c>).</param>
/// <param name="IndexName">
/// Index name as MongoDB sees it (e.g. <c>attributes.name.value_1</c>, <c>_id_</c>).
/// </param>
/// <param name="KeySpec">
/// Canonical JSON of the index's key spec (e.g. <c>{"attributes.name.value": 1}</c>). Lets
/// the operator confirm at a glance which query shape the index was built for without
/// having to remember the naming convention.
/// </param>
/// <param name="OpsCount">
/// Sum of <c>accesses.ops</c> across replica-set hosts since the earliest
/// <see cref="SinceUtc"/>. Counts plan-cache hits, not document fetches — an IXSCAN-then-FETCH
/// query counts once.
/// </param>
/// <param name="SinceUtc">
/// Earliest <c>accesses.since</c> across hosts. We take the earliest (not latest or average)
/// so an index that was added recently on a secondary doesn't get flagged as unused based on
/// the secondary's fresh age — we want the worst-case (longest) observation window.
/// </param>
/// <param name="AgeDays">
/// Days between <see cref="SinceUtc"/> and now, computed at collection time so the classifier
/// doesn't have to re-do clock arithmetic.
/// </param>
/// <param name="IsBuiltin">
/// <c>true</c> for the <c>_id_</c> index Mongo creates automatically. Builtin indexes are
/// never droppable; the surface renders the row read-only (no drop command, no copy button).
/// </param>
/// <param name="DropShellCommand">
/// Paste-ready mongosh literal for dropping the index, e.g.
/// <c>db.rt_entities.dropIndex("attributes.name.value_1")</c>. <c>null</c> when
/// <see cref="IsBuiltin"/> — <c>_id_</c> can't be dropped, and showing the command would
/// invite a footgun.
/// </param>
/// <param name="Status">Classification — see <see cref="IndexUsageStatus"/>.</param>
public sealed record IndexUsageEntry(
    string CollectionName,
    string IndexName,
    string KeySpec,
    long OpsCount,
    DateTimeOffset SinceUtc,
    int AgeDays,
    bool IsBuiltin,
    string? DropShellCommand,
    IndexUsageStatus Status);

/// <summary>
/// Classification outcome for a single <see cref="IndexUsageEntry"/>. Drives the Studio
/// surface's per-row colour and whether the drop button is enabled.
/// </summary>
public enum IndexUsageStatus
{
    /// <summary>
    /// MongoDB-managed (e.g. <c>_id_</c>). Never droppable, always surfaced as read-only.
    /// </summary>
    Builtin,

    /// <summary>
    /// <c>OpsCount == 0</c> and <c>AgeDays &gt;= MinAgeDays</c>. The strongest "consider
    /// dropping" signal — index has lived long enough to have been touched by every recurring
    /// query and never was.
    /// </summary>
    Unused,

    /// <summary>
    /// <c>OpsCount &gt; 0</c> but below <c>LowUsageOpsThreshold</c>, with
    /// <c>AgeDays &gt;= MinAgeDays</c>. Operator should investigate — could be a rare-but-real
    /// query path, or a leftover from a deleted feature that still gets hit once a quarter.
    /// </summary>
    LowUsage,

    /// <summary>
    /// Either ops above the threshold, or too young to judge. Nothing to act on.
    /// </summary>
    Used
}
