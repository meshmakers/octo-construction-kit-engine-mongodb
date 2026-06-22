namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// One captured slow MongoDB command. Kept in <see cref="SlowQueriesBuffer"/> for interactive
/// inspection via the Refinery Studio Diagnostics surface.
/// </summary>
/// <param name="Timestamp">UTC instant when the command's <c>CommandSucceededEvent</c>/<c>CommandFailedEvent</c> fired.</param>
/// <param name="CommandName">Driver-level command name (e.g. <c>find</c>, <c>aggregate</c>).</param>
/// <param name="Target">First BSON element value of the command — typically the target collection (e.g. <c>rt_entities</c>).</param>
/// <param name="Database">Database (= tenant ID) the command ran against. The trusted attribution dimension used to filter the per-tenant endpoint.</param>
/// <param name="DurationMs">Driver-reported duration in milliseconds.</param>
/// <param name="RequestId">MongoDB driver request id — correlates the Started / Succeeded / Failed events.</param>
/// <param name="CommandBsonPreview">Truncated BSON command body in JSON form, capped at <c>SlowQueryCommandPreviewBytes</c> UTF-8 bytes.</param>
/// <param name="Success"><c>true</c> if the command completed via <c>CommandSucceededEvent</c>; <c>false</c> if it failed.</param>
/// <param name="ErrorCode">For failures, the Mongo error code (e.g. 112 for WriteConflict). <c>null</c> when <see cref="Success"/> is <c>true</c>.</param>
/// <param name="Fingerprint">Structural fingerprint of the command (see <see cref="SlowQueryFingerprinter"/>) — used to group semantically-identical queries that differ only in literal values.</param>
/// <param name="Explain">
/// Latest async <c>explain()</c> result for this entry's fingerprint key, stamped at read time
/// from <see cref="SlowQueryExplainCache"/>. <c>null</c> until a capture has finished — the
/// driver-thread side never blocks on explain. See <see cref="SlowQueryExplainParser"/> for
/// the parsing pipeline.
/// </param>
public sealed record SlowQueryEntry(
    DateTimeOffset Timestamp,
    string CommandName,
    string Target,
    string Database,
    double DurationMs,
    int RequestId,
    string CommandBsonPreview,
    bool Success,
    string? ErrorCode,
    string Fingerprint,
    SlowQueryExplain? Explain = null);
