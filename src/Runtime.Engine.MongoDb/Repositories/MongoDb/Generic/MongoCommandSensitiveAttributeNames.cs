using System.Collections.Concurrent;
using System.Collections.Frozen;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     The CK attribute names whose values <see cref="MongoCommandSecretRedactor" /> masks in rendered MongoDB
///     commands (AB#8009): the debug command log, the slow-query WARN line, the slow-query buffer behind the
///     Studio diagnostics page and the explain preview.
/// </summary>
/// <remarks>
///     <para>
///         Two sources. <see cref="KnownCredentialNames" /> are credential fields of the platform's own models
///         that are masked without any CK knowledge (a host without a CK cache, a tenant whose cache is not
///         loaded yet, the explain preview). The CK cache adds, per tenant database, every attribute that the
///         model marks <c>access: Hidden</c>, <c>securitySensitive</c>, value type <c>Secret</c> or (on a
///         type) ownership <c>Secret</c>.
///     </para>
///     <para>
///         The set only grows: names found for one tenant database are masked in the commands of every
///         database this instance renders. Masking a same-named attribute of another tenant costs diagnostic
///         detail, never a leak. A tenant's names are read from the cache at most once per
///         <see cref="RefreshInterval" />, so a model imported later is picked up within that interval.
///     </para>
///     <para>
///         Names are attribute names as stored (camelCase, compared case-insensitively). The redactor matches
///         them only in the attribute context of a document (an element of an <c>attributes</c> sub-document, or
///         the path segment after <c>attributes.</c>), so a CK attribute called <c>filter</c> does not mask a
///         command's <c>filter</c>.
///     </para>
///     <para>
///         The CK cache is keyed by tenant id and the lookup uses the database name of the command, so the
///         model-derived names apply to tenants whose database name equals the tenant id (the platform default;
///         the diagnostics endpoint filters the slow-query list by the same rule). For any other database the
///         known credential names and the names already found apply.
///     </para>
/// </remarks>
internal sealed class MongoCommandSensitiveAttributeNames
{
    /// <summary>
    ///     Credential and token fields of the identity stores, masked regardless of model knowledge:
    ///     <list type="bullet">
    ///         <item><c>System.Identity/User</c>: <c>passwordHash</c>, <c>securityStamp</c>, <c>userTokens</c>
    ///         (authenticator key, recovery codes, OTP challenges).</item>
    ///         <item>Client / API resource: <c>clientSecrets</c>, <c>apiSecrets</c> (secret hashes).</item>
    ///         <item>Data Protection key ring: <c>xmlData</c>.</item>
    ///         <item>OpenIddict token: <c>payload</c> (protected token payload), <c>referenceId</c> (hashed
    ///         reference token id).</item>
    ///         <item>Server-side session: <c>ticket</c> (data-protected authentication ticket), <c>sessionKey</c>
    ///         (the key the session cookie carries).</item>
    ///         <item>Persisted grant: <c>data</c> (serialized grant), <c>grantKey</c> (hashed grant handle).</item>
    ///     </list>
    ///     <c>data</c>, <c>payload</c> and <c>ticket</c> are generic names: an attribute of the same name in any
    ///     other model is masked too (diagnostic detail lost, decided for AB#8009).
    /// </summary>
    public static readonly FrozenSet<string> KnownCredentialNames = new[]
    {
        "passwordHash",
        "securityStamp",
        "userTokens",
        "clientSecrets",
        "apiSecrets",
        "xmlData",
        "payload",
        "referenceId",
        "ticket",
        "sessionKey",
        "data",
        "grantKey"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Minimum time between two reads of the same tenant's names from the CK cache.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly Func<string, IReadOnlyCollection<string>?>? _tenantNames;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRead = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private volatile FrozenSet<string> _names = KnownCredentialNames;

    /// <summary>
    ///     Creates the name set for a host; without a CK cache only <see cref="KnownCredentialNames" /> apply.
    /// </summary>
    public MongoCommandSensitiveAttributeNames(ICkCacheService? ckCacheService, TimeProvider? timeProvider = null)
        : this(ckCacheService is null ? null : database => CollectFromCkCache(ckCacheService, database), timeProvider)
    {
    }

    /// <summary>
    ///     Test seam: <paramref name="tenantNames" /> answers the sensitive names of a database, or <c>null</c>
    ///     when it cannot tell yet (the CK cache of that tenant is not loaded).
    /// </summary>
    internal MongoCommandSensitiveAttributeNames(Func<string, IReadOnlyCollection<string>?>? tenantNames,
        TimeProvider? timeProvider)
    {
        _tenantNames = tenantNames;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     The names to mask in a command of <paramref name="database" />. Never throws.
    /// </summary>
    public IReadOnlySet<string> GetFor(string? database)
    {
        if (_tenantNames is null || string.IsNullOrEmpty(database) || database == "unknown")
        {
            return _names;
        }

        var now = _timeProvider.GetUtcNow();
        if (_lastRead.TryGetValue(database, out var last) && now - last < RefreshInterval)
        {
            return _names;
        }

        IReadOnlyCollection<string>? found;
        try
        {
            found = _tenantNames(database);
        }
        catch (Exception)
        {
            // The known names still apply; do not retry a failing cache on every command.
            _lastRead[database] = now;
            return _names;
        }

        if (found is null)
        {
            // Cache not loaded yet: ask again with the next command (the check is a dictionary lookup).
            return _names;
        }

        _lastRead[database] = now;
        if (found.Count > 0)
        {
            lock (_gate)
            {
                if (found.Any(name => !_names.Contains(name)))
                {
                    _names = _names.Union(found).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        return _names;
    }

    /// <summary>
    ///     The attribute names a tenant's CK cache marks as sensitive, or <c>null</c> when the tenant is not
    ///     loaded. Types contribute Hidden, securitySensitive, Secret-typed and Secret-owned attributes; records
    ///     contribute Hidden, securitySensitive and Secret-typed ones (a Secret-owned record member such as the
    ///     <c>Value</c> of a user token is covered by its owning attribute, and its name is too generic to mask
    ///     everywhere).
    /// </summary>
    internal static IReadOnlyCollection<string>? CollectFromCkCache(ICkCacheService cache, string tenantId)
    {
        if (!cache.IsTenantLoaded(tenantId))
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitionIsSensitive = new Dictionary<CkId<CkAttributeId>, bool>();

        foreach (var type in cache.GetCkTypes(tenantId))
        {
            Collect(cache, tenantId, type.AllAttributes.Values, includeSecretOwnership: true, names, definitionIsSensitive);
        }

        foreach (var record in cache.GetCkRecords(tenantId))
        {
            Collect(cache, tenantId, record.AllAttributes.Values, includeSecretOwnership: false, names, definitionIsSensitive);
        }

        return names;
    }

    private static void Collect(ICkCacheService cache, string tenantId, IEnumerable<CkTypeAttributeGraph> attributes,
        bool includeSecretOwnership, HashSet<string> names, Dictionary<CkId<CkAttributeId>, bool> definitionIsSensitive)
    {
        foreach (var attribute in attributes)
        {
            if (names.Contains(attribute.AttributeName))
            {
                continue;
            }

            if (attribute.Access == CkAttributeAccessDto.Hidden
                || attribute.ValueType == AttributeValueTypesDto.Secret
                || (includeSecretOwnership && attribute.Ownership == AttributeOwnershipDto.Secret)
                || IsDefinitionSensitive(cache, tenantId, attribute.CkAttributeId, definitionIsSensitive))
            {
                names.Add(attribute.AttributeName);
            }
        }
    }

    private static bool IsDefinitionSensitive(ICkCacheService cache, string tenantId, CkId<CkAttributeId> attributeId,
        Dictionary<CkId<CkAttributeId>, bool> definitionIsSensitive)
    {
        if (definitionIsSensitive.TryGetValue(attributeId, out var known))
        {
            return known;
        }

        bool sensitive;
        try
        {
            sensitive = cache.GetCkAttribute(tenantId, attributeId).SecuritySensitive;
        }
        catch (Exception)
        {
            sensitive = false;
        }

        definitionIsSensitive[attributeId] = sensitive;
        return sensitive;
    }
}
