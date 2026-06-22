namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// MongoDB index recommendation emitted for a slow query whose explain plan reported a
/// <c>COLLSCAN</c> (Stage 2C / AB#4220). Attached to <see cref="SlowQueryExplain"/> when the
/// suggester could extract at least one filter field from the original command. The
/// <see cref="ShellCommand"/> is intended for copy-paste execution in <c>mongosh</c> against
/// the affected tenant database.
/// </summary>
/// <param name="IndexName">
/// Generated index name following MongoDB's <c>field_direction[_field_direction]…</c>
/// convention. Capped at 127 bytes (Mongo's hard limit); names that would exceed it are
/// truncated and suffixed with the first 8 hex chars of a SHA-256 of the full name so
/// suggestions for similar shapes don't collide.
/// </param>
/// <param name="Fields">
/// Ordered field list per Mongo's ESR rule (Equality → Sort → Range). A compound index
/// honouring this order is selective for every prefix subset, which is what we want when one
/// suggestion has to serve potentially many filter shapes that share fingerprint.
/// </param>
/// <param name="ShellCommand">
/// Ready-to-run mongosh command, e.g.
/// <c>db.rt_entities.createIndex({"attributes.name.value": 1}, {name: "attributes_name_value_1"})</c>.
/// Field paths are JSON-string-quoted (dotted paths require quoting in mongosh).
/// </param>
/// <param name="Confidence">
/// Heuristic rating — see <see cref="SlowQueryIndexSuggestionConfidence"/>. A <c>Low</c>
/// suggestion is still emitted so the operator has a starting point; the <see cref="Notes"/>
/// list spells out the caveat.
/// </param>
/// <param name="Notes">
/// Short caveats that an operator needs before running the command (e.g. <c>"$or branches;
/// per-branch indexes may be more selective"</c>, <c>"$text operator detected — a text index
/// is required, not a regular index"</c>). Empty when the suggestion is unambiguous.
/// </param>
public sealed record SlowQueryIndexSuggestion(
    string IndexName,
    IReadOnlyList<SlowQueryIndexField> Fields,
    string ShellCommand,
    SlowQueryIndexSuggestionConfidence Confidence,
    IReadOnlyList<string> Notes);

/// <summary>
/// One field of a suggested compound index. <see cref="Direction"/> is conventionally
/// <c>1</c> (ascending) or <c>-1</c> (descending); equality fields default to <c>1</c>, sort
/// fields preserve whatever direction the original command specified (or default <c>1</c>
/// with a note when ambiguous).
/// </summary>
/// <param name="Name">Field path verbatim from the BSON filter (e.g. <c>attributes.name.value</c>).</param>
/// <param name="Direction"><c>1</c> (ascending) or <c>-1</c> (descending).</param>
/// <param name="Kind">Classification — equality, range, or sort key. Drives ESR ordering.</param>
public sealed record SlowQueryIndexField(
    string Name,
    int Direction,
    SlowQueryIndexFieldKind Kind);

/// <summary>
/// Per-field classification used to apply Mongo's ESR rule when ordering compound-index keys.
/// </summary>
public enum SlowQueryIndexFieldKind
{
    /// <summary>Equality predicate (<c>{a: 5}</c>, <c>{a: {$eq: 5}}</c>, <c>{a: {$in: [...]}}</c>).</summary>
    Equality,

    /// <summary>Sort key from the command's <c>sort</c> field.</summary>
    Sort,

    /// <summary>Range predicate (<c>{a: {$gt: 5}}</c> and friends).</summary>
    Range
}

/// <summary>
/// Confidence rating of an emitted suggestion — see field XML for the thresholds.
/// </summary>
public enum SlowQueryIndexSuggestionConfidence
{
    /// <summary>
    /// 4+ fields, contains <c>$or</c>/<c>$nor</c>, contains text/geo, or unrecognised shape.
    /// Suggestion still emitted as a starting point; <c>Notes</c> spells out the caveat.
    /// </summary>
    Low,

    /// <summary>Compound 2-3 fields, equality + at most one range / sort. ESR rule confidently applied.</summary>
    Medium,

    /// <summary>Single field, equality only, no <c>$or</c>. The classic missing-index case.</summary>
    High
}
