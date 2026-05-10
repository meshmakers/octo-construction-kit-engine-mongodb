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

    /// <summary>timestamp column name</summary>
    public const string Timestamp = "timestamp";

    /// <summary>ckTypeId column name</summary>
    public const string CkTypeId = "cktypeid";

    /// <summary>rtWellKnownName column name</summary>
    public const string RtWellKnownName = "rtwellknownname";

    /// <summary>rtCreationDateTime column name</summary>
    public const string RtCreationDateTime = "rtcreationdatetime";

    /// <summary>rtChangedDateTime column name</summary>
    public const string RtChangedDateTime = "rtchangeddatetime";

    /// <summary>
    /// Default stream data fields. Order matters where this is iterated to build SELECT lists or
    /// PK clauses — keep timestamp first for the primary key, the rest follow.
    /// </summary>
    public static readonly string[] DefaultStreamDataFields = [Timestamp, RtId, CkTypeId, RtWellKnownName, RtCreationDateTime, RtChangedDateTime];

    /// <summary>
    /// Checks if the given field name is a default stream data field (case-insensitive).
    /// </summary>
    public static bool IsDefaultField(string fieldName)
        => DefaultStreamDataFields.Any(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the canonical camelCase name of a default field, or null if not a default.
    /// </summary>
    public static string? GetDefaultFieldName(string fieldName)
        => DefaultStreamDataFields.FirstOrDefault(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Date time format
    /// </summary>
    public static readonly string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
    
    /// <summary>
    /// Default connection cache duration
    /// </summary>
    public static readonly TimeSpan DefaultConnectionCacheDuration = TimeSpan.FromMinutes(5);
}