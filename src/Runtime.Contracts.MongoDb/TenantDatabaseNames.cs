using System.Text;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Naming rules for tenant databases under an instance tenant-database prefix (AB#8797, Epic AB#4944).
/// </summary>
/// <remarks>
///     Two OctoMesh instances on one MongoDB server (different system databases) can create a tenant with the same
///     id when each instance prefixes its tenant databases, the way CrateDB schemas already carry
///     <c>StreamData:SchemaInstancePrefix</c>. The prefix is <see cref="Configuration.OctoSystemConfiguration.TenantDatabasePrefix" />;
///     empty (the default) keeps every name exactly as before.
/// </remarks>
public static class TenantDatabaseNames
{
    /// <summary>
    ///     Longest accepted prefix in UTF-8 bytes. MongoDB allows 63 bytes for a database name; the rest is left for
    ///     the tenant id. A derived name longer than 63 bytes is still refused by the create path.
    /// </summary>
    public const int MaxPrefixBytes = 30;

    /// <summary>
    ///     Normalizes a configured prefix the same way database names are normalized (trimmed, lower case,
    ///     culture-invariant). Null, empty and whitespace mean "no prefix" and return an empty string.
    /// </summary>
    public static string NormalizePrefix(string? prefix)
    {
        return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim().ToLowerInvariant();
    }

    /// <summary>
    ///     Checks a normalized prefix. Allowed are at most <see cref="MaxPrefixBytes" /> ASCII letters, digits,
    ///     <c>-</c> and <c>_</c> - the characters of a tenant id, so a derived name never contains a character
    ///     MongoDB refuses.
    /// </summary>
    /// <returns><c>null</c> when the prefix is valid (an empty prefix is valid), otherwise the reason.</returns>
    public static string? ValidatePrefix(string normalizedPrefix)
    {
        if (normalizedPrefix.Length == 0)
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(normalizedPrefix) > MaxPrefixBytes)
        {
            return $"it is longer than {MaxPrefixBytes} bytes";
        }

        if (normalizedPrefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            return "only ASCII letters, digits, '-' and '_' are allowed";
        }

        return null;
    }

    /// <summary>
    ///     The default database name of a tenant: <c>&lt;prefix&gt;&lt;tenantId&gt;</c>, normalized. Without a
    ///     prefix it is the normalized tenant id, the name clients use by convention today.
    /// </summary>
    public static string DeriveDefault(string? prefix, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return NormalizePrefix(prefix) + tenantId.Trim().ToLowerInvariant();
    }

    /// <summary>
    ///     Whether a database name may be used for a new tenant under the prefix: always without a prefix, otherwise
    ///     only when the normalized name starts with the prefix.
    /// </summary>
    public static bool CarriesPrefix(string? prefix, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        var normalizedPrefix = NormalizePrefix(prefix);
        return normalizedPrefix.Length == 0 ||
               databaseName.Trim().ToLowerInvariant().StartsWith(normalizedPrefix, StringComparison.Ordinal);
    }
}
