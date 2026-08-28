using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Computes the per-tenant CrateDB schema name and the schema-qualified table identifier used
/// across all stream-data DDL and DML. Each tenant lives in its own CrateDB schema (one cluster,
/// schema-per-tenant isolation) per the StreamData archive concept §4.
/// </summary>
internal static class TenantSchema
{
    /// <summary>
    /// Maximum length for a CrateDB schema identifier (the chosen budget; CrateDB hard-limit is 255
    /// bytes — we keep schema short so the combined "schema"."table" stays well under the limit
    /// even with long archive table names).
    /// </summary>
    public const int MaxSchemaLength = 63;

    /// <summary>
    /// Table name used by the legacy single-table-per-tenant stream data store (the only table
    /// shape in use until archives land in T7). Once archives are first-class, archive tables
    /// replace this and live alongside it in the same schema.
    /// </summary>
    public const string LegacyStreamDataTable = "streamData";

    private static readonly Regex NonAlphanumeric = new("[^A-Za-z0-9]+", RegexOptions.Compiled);

    // AB#4946 (Epic AB#4944): optional per-process instance prefix so two OctoMesh instances can
    // share one CrateDB cluster without colliding on identical tenant ids. Process-wide and
    // set-once by design — schema naming is inherently one-per-instance, and threading the value
    // through every static SQL builder would churn the whole DDL/DML surface for a constant.
    // Empty (the default) keeps every schema name byte-identical to the pre-AB#4946 behaviour —
    // REQUIRED for existing instances, whose schemas must not move.
    private static volatile string _instancePrefix = string.Empty;

    /// <summary>
    /// The active instance prefix ('' = today's un-prefixed naming). Set once at startup via
    /// <see cref="SetInstancePrefix"/> from <c>StreamDataConfiguration.SchemaInstancePrefix</c>.
    /// </summary>
    internal static string InstancePrefix => _instancePrefix;

    /// <summary>
    /// Configures the instance prefix. Idempotent for the same effective value; a CONFLICTING
    /// second value throws — two different prefixes inside one process would silently split the
    /// tenant's data across two schemas, so a misconfiguration must fail loud at startup.
    /// Null/empty keeps the un-prefixed naming (the backward-compatible default; existing
    /// instances must never set a prefix, or their schemas would move).
    /// </summary>
    public static void SetInstancePrefix(string? rawPrefix)
    {
        var cleaned = string.IsNullOrWhiteSpace(rawPrefix)
            ? string.Empty
            : NonAlphanumeric.Replace(rawPrefix, string.Empty).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(rawPrefix) && cleaned.Length == 0)
        {
            throw new ArgumentException(
                $"Schema instance prefix '{rawPrefix}' contains no alphanumeric characters and cannot be used.",
                nameof(rawPrefix));
        }

        var current = _instancePrefix;
        if (current == cleaned)
        {
            return;
        }

        if (current.Length != 0 && cleaned.Length != 0)
        {
            throw new InvalidOperationException(
                $"The CrateDB schema instance prefix is already set to '{current}' and cannot be changed to " +
                $"'{cleaned}' within the same process — check for conflicting StreamData configuration.");
        }

        // One of the two is empty: a late-arriving empty value (a second consumer without the
        // setting) must not clear an already-configured prefix, and a configured prefix may
        // arrive after an early empty initialization.
        if (cleaned.Length != 0)
        {
            _instancePrefix = cleaned;
        }
    }

    /// <summary>Test-only: resets the process-wide prefix so naming tests are order-independent.</summary>
    internal static void ResetInstancePrefixForTests() => _instancePrefix = string.Empty;

    /// <summary>
    /// Returns the schema name for the given tenant id. Strips non-alphanumeric characters,
    /// lowercases, and falls back to a SHA-256 hash suffix when the cleaned name exceeds
    /// <see cref="MaxSchemaLength"/>. With a configured instance prefix (AB#4946) the schema is
    /// <c>{prefix}_{tenant}</c>; without one the naming is byte-identical to the pre-prefix
    /// behaviour.
    /// </summary>
    public static string SchemaName(string tenantId)
    {
        return SchemaName(tenantId, _instancePrefix);
    }

    /// <summary>
    ///     Pure naming core (testable without touching the process-wide prefix state).
    /// </summary>
    internal static string SchemaName(string tenantId, string prefix)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        var cleaned = NonAlphanumeric.Replace(tenantId, string.Empty).ToLowerInvariant();
        if (cleaned.Length == 0)
        {
            throw new ArgumentException(
                $"Tenant id '{tenantId}' contains no alphanumeric characters and cannot be mapped to a CrateDB schema name.",
                nameof(tenantId));
        }

        if (prefix.Length == 0)
        {
            if (cleaned.Length <= MaxSchemaLength)
            {
                return cleaned;
            }

            var hash = ShortHash(cleaned);
            var keep = MaxSchemaLength - 1 - hash.Length;
            return cleaned.Substring(0, keep) + "_" + hash;
        }

        var combined = prefix + "_" + cleaned;
        if (combined.Length <= MaxSchemaLength)
        {
            return combined;
        }

        // Same hash-suffix fallback, with the prefix (and its separator) inside the budget. The
        // hash is over the cleaned tenant id — its job is per-tenant uniqueness; the prefix is
        // constant per instance.
        var overflowHash = ShortHash(cleaned);
        var keepTenant = MaxSchemaLength - prefix.Length - 2 - overflowHash.Length;
        return prefix + "_" + cleaned.Substring(0, keepTenant) + "_" + overflowHash;
    }

    /// <summary>
    /// Returns the fully-qualified, double-quoted identifier for the legacy stream-data table in
    /// the given tenant's schema, e.g. <c>"acmecorp"."streamData"</c>. Safe to embed directly into
    /// SQL templates that use <c>string.Format</c> with a single positional placeholder.
    /// </summary>
    public static string QualifiedLegacyTable(string tenantId)
    {
        return $"\"{SchemaName(tenantId)}\".\"{LegacyStreamDataTable}\"";
    }

    /// <summary>
    /// Returns the double-quoted schema identifier for the given tenant id, e.g. <c>"acmecorp"</c>.
    /// </summary>
    public static string QuotedSchema(string tenantId)
    {
        return $"\"{SchemaName(tenantId)}\"";
    }

    /// <summary>
    /// Returns the fully-qualified, double-quoted identifier for the per-archive table, e.g.
    /// <c>"acmecorp"."archive_65d5c447b420da3fb12381bc"</c>. Naming uses the archive runtime id so
    /// the table name is stable across renames of the archive's well-known name and unique even
    /// when two archives target the same CK type. Concept §4: archives live alongside the legacy
    /// table in the same tenant schema until the hard cut (T17) removes the legacy table.
    /// </summary>
    public static string QualifiedArchiveTable(string tenantId, string archiveRtId)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            throw new ArgumentException("archiveRtId must not be empty.", nameof(archiveRtId));
        }
        return $"\"{SchemaName(tenantId)}\".\"archive_{archiveRtId}\"";
    }

    /// <summary>
    /// Returns the unqualified per-archive table name (without schema prefix or quoting), e.g.
    /// <c>archive_65d5c447b420da3fb12381bc</c>. Used by introspection queries against system
    /// tables where the table name has to be passed as a plain parameter value rather than a SQL
    /// identifier.
    /// </summary>
    public static string ArchiveTableName(string archiveRtId)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            throw new ArgumentException("archiveRtId must not be empty.", nameof(archiveRtId));
        }
        return $"archive_{archiveRtId}";
    }

    private static string ShortHash(string value)
    {
#if NETSTANDARD2_0
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
#else
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
#endif
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
