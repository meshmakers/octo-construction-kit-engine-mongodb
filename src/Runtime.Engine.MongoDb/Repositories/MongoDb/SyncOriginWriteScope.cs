using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     The write rule of synced master data (<see cref="SyncOriginWriteRules" />) while the guarded write funnel of a
///     tenant repository applies a change set (<see cref="SyncOriginGuardedBulkRtMutation" />). Outside such a call there
///     is no scope, and the collections write exactly as before (maintenance repository, bulk import, migrations).
/// </summary>
/// <remarks>
///     The rule is folded into the write statements themselves, so the happy path costs no extra command: an update,
///     replace or delete of a runtime entity carries <see cref="WriteFilter{TDocument}" /> in its filter, and a copy the
///     session may not touch simply does not match. Only when a statement matched nothing does
///     <see cref="ThrowIfRefusedAsync{TDocument}" /> read once to tell a refused copy from a missing entity.
/// </remarks>
internal sealed class SyncOriginWriteScope
{
    private const string StoredField = "attributes.rtSyncOrigin";
    private const string StoredSourceField = "attributes.rtSyncOrigin.attributes.sourceTenantId";

    private static readonly AsyncLocal<SyncOriginWriteScope?> CurrentScope = new();

    private SyncOriginWriteScope(string? syncSourceTenantId)
    {
        SyncSourceTenantId = syncSourceTenantId;
    }

    /// <summary>The scope of the change set being applied, or <c>null</c> outside the guarded funnel.</summary>
    public static SyncOriginWriteScope? Current => CurrentScope.Value;

    /// <summary>The source tenant of a sync-writer session; <c>null</c> for every other session.</summary>
    public string? SyncSourceTenantId { get; }

    /// <summary>
    ///     Runs <paramref name="write" /> (the inner funnel) within the scope of the session's change set and restores
    ///     the previous scope afterwards. AsyncLocal: a concurrent call of another session never sees this scope, and
    ///     nothing after the call runs with it.
    /// </summary>
    public static async Task RunAsync(string? syncSourceTenantId, Func<Task> write)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new SyncOriginWriteScope(syncSourceTenantId);
        try
        {
            await write().ConfigureAwait(false);
        }
        finally
        {
            CurrentScope.Value = previous;
        }
    }

    /// <summary>
    ///     The clause every update, replace and delete of a runtime entity adds to its filter within the scope, or
    ///     <c>null</c> outside it and for other documents: an entity without an origin for a plain session, a copy of
    ///     its own source for a sync-writer session.
    /// </summary>
    public static FilterDefinition<TDocument>? WriteFilter<TDocument>()
    {
        var scope = Current;
        return scope == null ? null : FilterFor<TDocument>(scope.SyncSourceTenantId);
    }

    /// <summary>
    ///     The same clause for a session outside the scope (the selection of a filter write, see <c>Mutation</c>): the
    ///     entities without an origin for a plain session (<paramref name="syncSourceTenantId" /> <c>null</c>), the copies
    ///     of that source for a sync-writer session; <c>null</c> for other documents.
    /// </summary>
    public static FilterDefinition<TDocument>? FilterFor<TDocument>(string? syncSourceTenantId)
    {
        if (!typeof(RtEntity).IsAssignableFrom(typeof(TDocument)))
        {
            return null;
        }

        return syncSourceTenantId == null
            ? new BsonDocument(StoredField, BsonNull.Value)
            : new BsonDocument(StoredSourceField, syncSourceTenantId);
    }

    /// <summary>
    ///     After a statement with <see cref="WriteFilter{TDocument}" /> matched nothing: refuses when a stored entity
    ///     with that id is one the session may not touch (one read, only on this path). Otherwise returns, and the caller
    ///     reports its usual outcome (not found, version conflict, guard skip).
    /// </summary>
    public static async Task ThrowIfRefusedAsync<TDocument>(IMongoCollection<TDocument> collection,
        IClientSessionHandle sessionHandle, FilterDefinition<TDocument> idFilter, string operation)
    {
        var scope = Current;
        if (scope == null || !typeof(RtEntity).IsAssignableFrom(typeof(TDocument)))
        {
            return;
        }

        FilterDefinition<TDocument> refused = scope.SyncSourceTenantId == null
            ? new BsonDocument(StoredField, new BsonDocument("$ne", BsonNull.Value))
            : new BsonDocument(StoredSourceField, new BsonDocument("$ne", scope.SyncSourceTenantId));
        var stored = await collection
            .Find(sessionHandle, Builders<TDocument>.Filter.And(idFilter, refused))
            .Limit(1)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (stored is RtEntity entity)
        {
            var operationResult = new OperationResult();
            operationResult.AddMessage(scope.Refusal(entity, operation));
            RuntimeRepositoryException.ThrowIfOperationResultError(operationResult);
        }
    }

    private OperationMessage Refusal(RtEntity stored, string operation)
    {
        var storedSource = SyncOriginRefusals.SourceOf(stored.Attributes);
        var hasOrigin = SyncOriginRefusals.HasOrigin(stored.Attributes);
        var reason = SyncSourceTenantId == null
            ? $"it is master data synced from tenant '{storedSource ?? "(unknown)"}'; change or delete it there"
            : hasOrigin
                ? $"it is a copy of tenant '{storedSource ?? "(unknown)"}', not of the sync of tenant '{SyncSourceTenantId}'"
                : $"it is no synced copy; the sync of tenant '{SyncSourceTenantId}' never changes or deletes it";
        return SyncOriginRefusals.Message(operation, stored.RtId.ToString(),
            stored.CkTypeId?.FullName ?? "(unknown)", storedSource ?? SyncSourceTenantId, reason);
    }

}

/// <summary>The refusal messages and origin readers of <see cref="SyncOriginWriteRules" />.</summary>
internal static class SyncOriginRefusals
{
    public static OperationMessage Message(string operation, string rtId, string ckTypeId, string? sourceTenantId,
        string reason)
    {
        return new OperationMessage(MessageLevel.Error, $"{ckTypeId}@{rtId}", SyncOriginWriteRules.MessageNumber,
            $"Write refused ({SyncOriginWriteRules.ErrorCode}): the {operation} of entity '{rtId}' of type '{ckTypeId}' is refused because {reason}.",
            new Dictionary<string, string>
            {
                { "code", SyncOriginWriteRules.ErrorCode },
                { "operation", operation },
                { "rtId", rtId },
                { "ckTypeId", ckTypeId },
                { "sourceTenantId", sourceTenantId ?? string.Empty },
                { "reason", reason }
            });
    }

    public static bool IsOriginName(string? name) =>
        string.Equals(name, SyncOriginWriteRules.AttributeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     A dotted path into the origin (<c>RtSyncOrigin.SourceTenantId</c>, any case): the update statement writes it
    ///     as <c>attributes.rtSyncOrigin.…</c>, so it changes the origin like the attribute itself.
    /// </summary>
    public static bool IsOriginMemberPath(string? name) =>
        name != null && name.StartsWith(SyncOriginWriteRules.AttributeName + ".", StringComparison.OrdinalIgnoreCase);

    public static bool IsOriginNameOrMemberPath(string? name) => IsOriginName(name) || IsOriginMemberPath(name);

    public static bool HasOrigin(IReadOnlyDictionary<string, object?> attributes) =>
        attributes.Any(a => IsOriginName(a.Key) && a.Value != null);

    /// <summary>The source tenant of the origin among <paramref name="attributes" />, or <c>null</c>.</summary>
    public static string? SourceOf(IReadOnlyDictionary<string, object?> attributes)
    {
        foreach (var (key, value) in attributes)
        {
            if (IsOriginName(key))
            {
                return value is RtTypeWithAttributes record ? SourceOfRecord(record) : null;
            }
        }

        return null;
    }

    /// <summary>The source tenant an origin record names, or <c>null</c>.</summary>
    public static string? SourceOfRecord(RtTypeWithAttributes record)
    {
        foreach (var (key, value) in record.Attributes)
        {
            if (string.Equals(key, SyncOriginWriteRules.SourceTenantIdMember, StringComparison.OrdinalIgnoreCase))
            {
                return value as string;
            }
        }

        return null;
    }
}
