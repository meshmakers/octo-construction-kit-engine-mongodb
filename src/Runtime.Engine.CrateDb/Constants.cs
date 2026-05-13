namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Constants for the stream data service.
/// </summary>
public static class Constants
{
    // Per-archive tables use lower-case identifiers — CrateDB has known case-preservation
    // quirks for quoted mixed-case identifiers in some contexts (notably EXCLUDED."Col" inside
    // ON CONFLICT DO UPDATE clauses), so all physical column names are canonicalised to
    // lower-case. The API surface (Path values, GraphQL projections, query DSL) still carries
    // the original PascalCase / dotted form.

    /// <summary>rtId column name</summary>
    public const string RtId = "rtid";

    /// <summary>timestamp column name (raw + rollup archive tables only)</summary>
    public const string Timestamp = "timestamp";

    /// <summary>
    /// Inclusive window-start column for time-range archive tables. Mutually exclusive with
    /// <see cref="Timestamp"/>: raw and rollup tables emit <c>timestamp</c>, time-range tables
    /// emit <c>window_start</c> + <see cref="WindowEnd"/>.
    /// </summary>
    public const string WindowStart = "window_start";

    /// <summary>Exclusive window-end column for time-range archive tables.</summary>
    public const string WindowEnd = "window_end";

    /// <summary>
    /// Boolean flag on time-range rows that flips to <c>TRUE</c> on every conflict-upsert. Lets
    /// dashboards detect retro-corrections of externally-aggregated values without log-diving.
    /// Monotonic — once <c>true</c>, stays <c>true</c>.
    /// </summary>
    public const string WasUpdated = "was_updated";

    /// <summary>ckTypeId column name</summary>
    public const string CkTypeId = "cktypeid";

    /// <summary>rtWellKnownName column name</summary>
    public const string RtWellKnownName = "rtwellknownname";

    /// <summary>rtCreationDateTime column name</summary>
    public const string RtCreationDateTime = "rtcreationdatetime";

    /// <summary>rtChangedDateTime column name</summary>
    public const string RtChangedDateTime = "rtchangeddatetime";

    /// <summary>
    /// Default stream data fields for raw archives. Order matters where this is iterated to build
    /// SELECT lists or PK clauses — keep timestamp first for the primary key, the rest follow.
    /// </summary>
    public static readonly string[] DefaultStreamDataFields = [Timestamp, RtId, CkTypeId, RtWellKnownName, RtCreationDateTime, RtChangedDateTime];

    /// <summary>
    /// Default stream data fields for windowed (rollup + time-range) archives. Replaces the single
    /// <see cref="Timestamp"/> column with the <c>(window_start, window_end, was_updated)</c>
    /// triple. The Rt* columns are identical to <see cref="DefaultStreamDataFields"/>. Order
    /// matches the windowed table's PK ordering (window_start first).
    /// </summary>
    public static readonly string[] DefaultWindowedStreamDataFields =
        [WindowStart, WindowEnd, WasUpdated, RtId, CkTypeId, RtWellKnownName, RtCreationDateTime, RtChangedDateTime];

    /// <summary>
    /// Returns the appropriate default-fields set for the given storage shape. Pass
    /// <paramref name="usesWindowedStorage"/>=true for rollup and time-range archives.
    /// </summary>
    public static IReadOnlyList<string> GetDefaultStreamDataFields(bool usesWindowedStorage)
        => usesWindowedStorage ? DefaultWindowedStreamDataFields : DefaultStreamDataFields;

    /// <summary>
    /// Checks if the given field name is a default stream data field (case-insensitive). Considers
    /// both raw and windowed defaults — used by code that has to be agnostic to the archive shape
    /// (e.g. data-stream attribute registration, which must not shadow any potential default).
    /// </summary>
    public static bool IsDefaultField(string fieldName)
        => DefaultStreamDataFields.Any(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase))
        || DefaultWindowedStreamDataFields.Any(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the canonical camelCase name of a default field, or null if not a default. Searches
    /// both raw and windowed default sets.
    /// </summary>
    public static string? GetDefaultFieldName(string fieldName)
        => DefaultStreamDataFields.FirstOrDefault(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase))
        ?? DefaultWindowedStreamDataFields.FirstOrDefault(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Date time format
    /// </summary>
    public static readonly string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
    
    /// <summary>
    /// Default connection cache duration
    /// </summary>
    public static readonly TimeSpan DefaultConnectionCacheDuration = TimeSpan.FromMinutes(5);
}