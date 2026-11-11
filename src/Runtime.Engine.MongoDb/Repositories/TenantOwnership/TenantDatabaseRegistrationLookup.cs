using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     Cross-instance view of the tenant registries on the MongoDB server (AB#8796): which system
///     databases register a given tenant database, and whether a database is a system database at
///     all. The ownership marker (<see cref="TenantOwnershipStore" />) only exists once a database
///     has been created, attached or resolved after AB#4945; this lookup covers the databases that
///     predate it, because another instance's registration is visible in that instance's system
///     database even when the tenant database itself carries no marker.
/// </summary>
/// <remarks>
///     <para>
///         Reads with the admin client and never writes. Every call takes a fresh snapshot of the
///         server (database list, then each database's tenant registry), so it is meant for admin
///         paths — attach, restore, the list/stamp operation — not for the per-request resolve.
///     </para>
///     <para>
///         A <b>system database</b> is a database that carries the system-database marker
///         (<see cref="SystemDatabaseMarkerStore" />, AB#8672), or one that holds a tenant registry with
///         at least one row, carries no ownership marker (a marker is never written into a system
///         database) and is not itself registered as a tenant database by any other registry on the
///         server. The marker recognises the system database of an instance without any tenant; the
///         registry rule stays as the fallback for an instance that has not run a version that stamps
///         the marker. Its last condition separates a system database from a tenant database that keeps
///         parent-local rows for its own child tenants: such a parent is always registered in its
///         instance's system database.
///     </para>
///     <para>
///         All names are compared and returned normalized (trimmed, lower case), like everywhere else
///         in the tenant namespace gate.
///     </para>
/// </remarks>
internal sealed class TenantDatabaseRegistrationLookup
{
    /// <summary>The collection of the System/Tenant CK type (a collection root).</summary>
    internal const string TenantRegistryCollectionName = "RtEntity_SystemTenant";

    private const string DatabaseNameAttribute = "databaseName";

    private const string ParentTenantIdAttribute = "parentTenantId";

    private static readonly HashSet<string> MongoInternalDatabases =
        new(StringComparer.OrdinalIgnoreCase) { "admin", "local", "config" };

    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILogger<TenantDatabaseRegistrationLookup> _logger;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;

    public TenantDatabaseRegistrationLookup(IAdminRepositoryAccess adminRepositoryAccess,
        IOptions<OctoSystemConfiguration> systemConfiguration, ILogger<TenantDatabaseRegistrationLookup> logger)
    {
        _adminRepositoryAccess = adminRepositoryAccess;
        _systemConfiguration = systemConfiguration;
        _logger = logger;
    }

    /// <summary>
    ///     Returns the system databases on the server whose tenant registry has a row for
    ///     <paramref name="databaseName" />, normalized and sorted. The own instance's system database is
    ///     included when it registers the database; callers filter it out themselves.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindRegisteringSystemDatabasesAsync(string databaseName,
        CancellationToken cancellationToken = default)
    {
        var registrations = await GetRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        return registrations.GetRegisteringSystemDatabases(databaseName);
    }

    /// <summary>Whether <paramref name="databaseName" /> is a system database of some instance on the server.</summary>
    public async Task<bool> IsSystemDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        var registrations = await GetRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        return registrations.IsSystemDatabase(databaseName);
    }

    /// <summary>
    ///     Takes one snapshot of all tenant registries on the server. Use it when one operation needs
    ///     many answers (the list/stamp operation); the two single-question methods call it as well.
    /// </summary>
    public async Task<TenantDatabaseRegistrations> GetRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        // The admin client of the own system database reaches every database on the server.
        var serverClient = GetDatabase(Normalize(_systemConfiguration.Value.SystemDatabaseName)).Client;
        var databaseNames = await (await serverClient.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var registries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var markedDatabases = new HashSet<string>(StringComparer.Ordinal);
        var systemMarkedDatabases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var storedName in databaseNames.Where(n => !MongoInternalDatabases.Contains(n)))
        {
            var normalizedName = Normalize(storedName);
            var database = serverClient.GetDatabase(storedName);

            var collectionNames = await (await database.ListCollectionNamesAsync(
                    new ListCollectionNamesOptions
                    {
                        Filter = Builders<BsonDocument>.Filter.In("name",
                            new[]
                            {
                                TenantRegistryCollectionName, TenantOwnershipStore.CollectionName,
                                SystemDatabaseMarkerStore.CollectionName
                            })
                    }, cancellationToken).ConfigureAwait(false))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (collectionNames.Contains(TenantOwnershipStore.CollectionName) &&
                await database.GetCollection<BsonDocument>(TenantOwnershipStore.CollectionName)
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", TenantOwnershipRecord.DocumentId))
                    .AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                markedDatabases.Add(normalizedName);
            }

            if (collectionNames.Contains(SystemDatabaseMarkerStore.CollectionName) &&
                await database.GetCollection<BsonDocument>(SystemDatabaseMarkerStore.CollectionName)
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", SystemDatabaseMarkerStore.DocumentId))
                    .AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                systemMarkedDatabases.Add(normalizedName);
            }

            if (!collectionNames.Contains(TenantRegistryCollectionName))
            {
                continue;
            }

            var rows = await database.GetCollection<BsonDocument>(TenantRegistryCollectionName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var registered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var registeredName = ReadDatabaseName(row);
                if (registeredName != null && !string.Equals(registeredName, normalizedName, StringComparison.Ordinal))
                {
                    registered.Add(registeredName);
                }
            }

            if (registered.Count > 0)
            {
                // Merged: two stored names that differ only in case are one database for the gate.
                if (registries.TryGetValue(normalizedName, out var existing))
                {
                    existing.UnionWith(registered);
                }
                else
                {
                    registries[normalizedName] = registered;
                }
            }
        }

        var registeredAnywhere = new HashSet<string>(registries.Values.SelectMany(v => v), StringComparer.Ordinal);
        var systemRegistries = registries
            .Where(r => systemMarkedDatabases.Contains(r.Key) ||
                        (!markedDatabases.Contains(r.Key) && !registeredAnywhere.Contains(r.Key)))
            .ToDictionary(r => r.Key, r => (IReadOnlySet<string>)r.Value, StringComparer.Ordinal);

        // AB#8672: a marked system database without any tenant has no registry rows.
        foreach (var systemDatabase in systemMarkedDatabases.Where(d => !systemRegistries.ContainsKey(d)))
        {
            systemRegistries[systemDatabase] = new HashSet<string>(StringComparer.Ordinal);
        }

        _logger.LogDebug(
            "Tenant registry snapshot: {DatabaseCount} databases on the server, {RegistryCount} with a tenant " +
            "registry, {SystemCount} system databases", databaseNames.Count, registries.Count, systemRegistries.Count);

        return new TenantDatabaseRegistrations(systemRegistries);
    }

    /// <summary>
    ///     Returns the parent tenant ids of the rows in the tenant registry of <paramref name="databaseName" /> itself,
    ///     normalized; empty when the database holds no registry. Rows without a parent id are skipped. Used by the
    ///     delete guard (AB#8807) to tell the registry of a parent tenant (its rows name that tenant as parent) from a
    ///     system database that a corrupt row of this instance registers as a tenant database, which
    ///     <see cref="TenantDatabaseRegistrations.IsSystemDatabase" /> cannot see because the row hides it.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetRegistryParentTenantIdsAsync(string databaseName,
        CancellationToken cancellationToken = default)
    {
        var database = GetDatabase(Normalize(databaseName));
        var rows = await database.GetCollection<BsonDocument>(TenantRegistryCollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var parent = ReadStringAttribute(row, ParentTenantIdAttribute);
            if (parent != null)
            {
                parents.Add(parent);
            }
        }

        return parents;
    }

    /// <summary>
    ///     Reads the registered database name of a System/Tenant row. CK attributes are stored under
    ///     <c>attributes</c> in camelCase; a top-level field is accepted as well so a storage-layout
    ///     change cannot make every registration invisible without a failing test.
    /// </summary>
    private static string? ReadDatabaseName(BsonDocument row) => ReadStringAttribute(row, DatabaseNameAttribute);

    private static string? ReadStringAttribute(BsonDocument row, string attributeName)
    {
        if (row.TryGetValue("attributes", out var attributes) && attributes.IsBsonDocument &&
            attributes.AsBsonDocument.TryGetValue(attributeName, out var nested) && nested.IsString)
        {
            return Normalize(nested.AsString);
        }

        if (row.TryGetValue(attributeName, out var topLevel) && topLevel.IsString)
        {
            return Normalize(topLevel.AsString);
        }

        return null;
    }

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);
        var repository = (MongoRepository)client.GetRepository(databaseName);
        return repository.Database;
    }

    internal static string Normalize(string databaseName) => databaseName.Trim().ToLowerInvariant();
}

/// <summary>
///     One snapshot of the tenant registries of all system databases on a MongoDB server (AB#8796),
///     see <see cref="TenantDatabaseRegistrationLookup" />.
/// </summary>
internal sealed class TenantDatabaseRegistrations
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _registriesBySystemDatabase;

    public TenantDatabaseRegistrations(IReadOnlyDictionary<string, IReadOnlySet<string>> registriesBySystemDatabase)
    {
        _registriesBySystemDatabase = registriesBySystemDatabase;
    }

    /// <summary>The normalized names of all system databases found on the server.</summary>
    public IReadOnlyCollection<string> SystemDatabases => _registriesBySystemDatabase.Keys.ToList();

    /// <summary>Whether <paramref name="databaseName" /> is one of the system databases.</summary>
    public bool IsSystemDatabase(string databaseName) =>
        _registriesBySystemDatabase.ContainsKey(TenantDatabaseRegistrationLookup.Normalize(databaseName));

    /// <summary>The system databases that register <paramref name="databaseName" />, normalized and sorted.</summary>
    public IReadOnlyList<string> GetRegisteringSystemDatabases(string databaseName)
    {
        var normalized = TenantDatabaseRegistrationLookup.Normalize(databaseName);
        return _registriesBySystemDatabase
            .Where(r => r.Value.Contains(normalized))
            .Select(r => r.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
