using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     Reads and writes the system-database marker (AB#8672): one document in the SYSTEM database
///     itself that says "this database is the system database of an OctoMesh instance". Without it a
///     system database is only recognised by the rows of its tenant registry
///     (<see cref="TenantDatabaseRegistrationLookup" />), so the system database of an instance without
///     any tenant looked like a free database to the other instances on the same MongoDB server — and a
///     restore or attach of another instance could overwrite it.
/// </summary>
/// <remarks>
///     <para>
///         The marker is written when the system tenant is created and once per start of a service
///         that ensures the System CK model (the identity service on every start), so an instance that
///         predates the marker stamps its own system database as soon as it runs this version. Stamping
///         is idempotent: the first stamp time is kept.
///     </para>
///     <para>
///         The collection is listed in <c>InfrastructureCollections</c>: a database that holds nothing
///         but the marker is still an infrastructure shell and stays bootstrappable (AB#4854).
///     </para>
/// </remarks>
internal class SystemDatabaseMarkerStore
{
    internal const string CollectionName = InfrastructureCollections.SystemDatabaseMarker;

    /// <summary>Fixed document id — the collection holds exactly one marker.</summary>
    internal const string DocumentId = "systemDatabase";

    private const string SystemDatabaseNameField = "systemDatabaseName";
    private const string SystemTenantIdField = "systemTenantId";
    private const string StampedAtUtcField = "stampedAtUtc";

    private readonly IAdminRepositoryAccess _adminRepositoryAccess;

    public SystemDatabaseMarkerStore(IAdminRepositoryAccess adminRepositoryAccess)
    {
        _adminRepositoryAccess = adminRepositoryAccess;
    }

    /// <summary>
    ///     Stamps <paramref name="systemDatabaseName" /> as a system database. Insert-if-absent: a second
    ///     call changes nothing, so the marker keeps the time of the first stamp. Virtual for the tests of a
    ///     failing stamp (the instance still starts and reports <c>Degraded</c>).
    /// </summary>
    public virtual async Task StampIfAbsentAsync(string systemDatabaseName, string systemTenantId,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TenantDatabaseRegistrationLookup.Normalize(systemDatabaseName);
        var update = Builders<BsonDocument>.Update
            .SetOnInsert(SystemDatabaseNameField, normalizedName)
            .SetOnInsert(SystemTenantIdField, systemTenantId)
            .SetOnInsert(StampedAtUtcField, DateTime.UtcNow);

        await GetCollection(normalizedName).UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocumentId),
            update, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     When <paramref name="databaseName" /> was stamped as a system database, or null when it carries
    ///     no system-database marker.
    /// </summary>
    public async Task<DateTime?> GetStampedAtUtcAsync(string databaseName,
        CancellationToken cancellationToken = default)
    {
        var marker = await GetCollection(TenantDatabaseRegistrationLookup.Normalize(databaseName))
            .Find(Builders<BsonDocument>.Filter.Eq("_id", DocumentId))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return marker != null && marker.TryGetValue(StampedAtUtcField, out var stampedAt) &&
               stampedAt.IsValidDateTime
            ? stampedAt.ToUniversalTime()
            : null;
    }

    /// <summary>
    ///     Removes the marker. The restore calls it on the restored copy: a dump of a system database
    ///     restored as a tenant database must not make that tenant database look like a system database.
    /// </summary>
    public async Task RemoveAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        await GetCollection(TenantDatabaseRegistrationLookup.Normalize(databaseName))
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocumentId), cancellationToken)
            .ConfigureAwait(false);
    }

    private IMongoCollection<BsonDocument> GetCollection(string databaseName)
    {
        var client = _adminRepositoryAccess.GetRepositoryClient(databaseName);
        var repository = (MongoRepository)client.GetRepository(databaseName);
        return repository.Database.GetCollection<BsonDocument>(CollectionName);
    }
}
