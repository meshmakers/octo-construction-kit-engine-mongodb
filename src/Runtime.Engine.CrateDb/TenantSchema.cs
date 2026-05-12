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

    /// <summary>
    /// Returns the schema name for the given tenant id. Strips non-alphanumeric characters,
    /// lowercases, and falls back to a SHA-256 hash suffix when the cleaned name exceeds
    /// <see cref="MaxSchemaLength"/>.
    /// </summary>
    public static string SchemaName(string tenantId)
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

        if (cleaned.Length <= MaxSchemaLength)
        {
            return cleaned;
        }

        var hash = ShortHash(cleaned);
        var keep = MaxSchemaLength - 1 - hash.Length;
        return cleaned.Substring(0, keep) + "_" + hash;
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
