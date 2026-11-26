using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.Runtime.Engine.Secrets;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     The write rule of synced master data (<see cref="SyncOriginWriteRules" />) around the write funnel of a tenant
///     repository. Every insert, update, replace, delete and archive of the repository - by id, by filter and in
///     <c>ApplyChanges</c> - reaches the database through <see cref="IBulkRtMutation" />, so the rule holds once, here.
/// </summary>
/// <remarks>
///     <para>
///         Rule 1, on the change set without a read: what it writes into the origin. A plain session never writes it;
///         a sync-writer session inserts and replaces only with its own origin and never writes another one.
///     </para>
///     <para>
///         Rule 2, which stored entities the change set may touch, is folded into the write statements: within
///         <see cref="SyncOriginWriteScope" /> every update, replace and delete of a runtime entity carries the scope's
///         filter, so the happy path of an update or delete costs no extra command. The changes with a side effect
///         before their write statement, or without an id filter, are judged here before the write (one read per type
///         for those changes): an archive (the entity's associations are archived first), every replace (the engine
///         deletes and uploads the entity's linked binaries first), an update that writes a linked binary (the bytes are
///         uploaded or replaced first) and an insert in bulk mode (an upsert on the id, which would overwrite a stored
///         entity).
///     </para>
///     A refusal leaves the database as it was, also without a transaction. A failed read fails the write.
/// </remarks>
internal sealed class SyncOriginGuardedBulkRtMutation(IBulkRtMutation inner) : IBulkRtMutation
{
    public ISecretWriteNormalizer SecretWriteNormalizer => inner.SecretWriteNormalizer;

    public Task ApplyChangesAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        ICkCacheService ckCacheService, IReadOnlyList<IEntityUpdateInfo<RtEntity>> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList, BulkRtMutationOptions options)
    {
        return ApplyChangesAsync(session, repositoryDataSource, ckCacheService, entityUpdateInfoList,
            associationUpdateInfoList, options, new OperationResult());
    }

    public async Task ApplyChangesAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        ICkCacheService ckCacheService, IReadOnlyList<IEntityUpdateInfo<RtEntity>> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList, BulkRtMutationOptions options,
        OperationResult callerResult)
    {
        var syncSource = (session as OctoSession)?.SyncWriterSourceTenantId;
        if (entityUpdateInfoList.Count > 0)
        {
            EnsureWrittenOriginsAllowed(syncSource, entityUpdateInfoList);
            await EnsureUpfrontJudgedChangesAllowedAsync(session, syncSource, repositoryDataSource, ckCacheService,
                entityUpdateInfoList, options).ConfigureAwait(false);
        }

        await SyncOriginWriteScope.RunAsync(syncSource, () => inner.ApplyChangesAsync(session, repositoryDataSource,
            ckCacheService, entityUpdateInfoList, associationUpdateInfoList, options, callerResult)).ConfigureAwait(false);
    }

    /// <summary>Rule 1, without a read: what the change set writes into the origin.</summary>
    private static void EnsureWrittenOriginsAllowed(string? syncSource,
        IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates)
    {
        var result = new OperationResult();
        foreach (var update in updates)
        {
            if (update.ModOption == EntityModOptions.Delete)
            {
                continue;
            }

            var written = WrittenOrigin(update, out var writesOrigin);
            if (syncSource == null)
            {
                // A null value on an entity without an origin changes nothing; on a copy the write filter refuses it.
                if (writesOrigin && (written != null || WritesOriginItemOrSecret(update)))
                {
                    result.AddMessage(Refusal(update, null,
                        $"it sets {SyncOriginWriteRules.AttributeName}, which only the master-data sync writes"));
                }

                continue;
            }

            // The sync writer: an insert or replace carries its own origin, an update writes no other origin.
            var mustCarryOrigin = update.ModOption is EntityModOptions.Insert or EntityModOptions.Replace;
            if ((mustCarryOrigin || writesOrigin) && !NamesSource(written, syncSource))
            {
                result.AddMessage(Refusal(update, syncSource,
                    $"the sync of tenant '{syncSource}' writes only copies with its own {SyncOriginWriteRules.AttributeName}"));
            }
        }

        RuntimeRepositoryException.ThrowIfOperationResultError(result);
    }

    /// <summary>
    ///     Rule 2 for the changes the write filter cannot cover, judged before anything is written: an archive (its
    ///     associations are archived before the entity), every replace (the engine deletes and uploads linked binaries
    ///     before the replace statement; without a transaction those bytes would be gone before the refusal), an update
    ///     that writes a linked binary (bytes uploaded or replaced before the update statement) and an insert in bulk
    ///     mode (an upsert on the id). One read per type, only when the change set has such changes.
    /// </summary>
    private static async Task EnsureUpfrontJudgedChangesAllowedAsync(IOctoSession session, string? syncSource,
        IRepositoryDataSource repositoryDataSource, ICkCacheService ckCacheService,
        IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, BulkRtMutationOptions options)
    {
        var judged = updates
            .Select(u => (Update: u, RtId: JudgedRtId(u)))
            .Where(x => x.RtId != null &&
                        IsJudgedBeforeTheWrite(x.Update, options, repositoryDataSource.TenantId, ckCacheService))
            .ToList();
        if (judged.Count == 0)
        {
            return;
        }

        var result = new OperationResult();
        foreach (var group in judged.GroupBy(x => x.Update.CkTypeId))
        {
            var ids = group.Select(x => x.RtId!.Value).Distinct().ToList();
            var stored = await ReadStoredAsync(session, repositoryDataSource, ckCacheService, group.Key, ids)
                .ConfigureAwait(false);
            foreach (var (update, rtId) in group)
            {
                stored.TryGetValue(rtId!.Value, out var entity);
                var hasOrigin = entity != null && SyncOriginRefusals.HasOrigin(entity.Attributes);
                var storedSource = entity == null ? null : SyncOriginRefusals.SourceOf(entity.Attributes);
                if (syncSource == null)
                {
                    if (hasOrigin)
                    {
                        result.AddMessage(Refusal(update, storedSource,
                            $"it is master data synced from tenant '{storedSource ?? "(unknown)"}'; change or delete it there"));
                    }
                }
                else if (entity != null && (!hasOrigin || !SameTenant(storedSource, syncSource)))
                {
                    result.AddMessage(Refusal(update, storedSource, hasOrigin
                        ? $"it is a copy of tenant '{storedSource ?? "(unknown)"}', not of the sync of tenant '{syncSource}'"
                        : $"it is no synced copy; the sync of tenant '{syncSource}' never changes or deletes it"));
                }
            }
        }

        RuntimeRepositoryException.ThrowIfOperationResultError(result);
    }

    /// <summary>The id a change targets: the update info's, else (an insert) the entity's; <c>null</c> for a new id.</summary>
    private static OctoObjectId? JudgedRtId(IEntityUpdateInfo<RtEntity> update)
    {
        var rtId = update.RtId ?? update.RtEntity?.RtId;
        return rtId == null || rtId.Value == default ? null : rtId;
    }

    private static bool IsJudgedBeforeTheWrite(IEntityUpdateInfo<RtEntity> update, BulkRtMutationOptions options,
        string tenantId, ICkCacheService ckCacheService) => update.ModOption switch
    {
        EntityModOptions.Delete => options.DeleteStrategy == DeleteStrategies.Archive,
        EntityModOptions.Replace => true,
        EntityModOptions.Insert => options.UseBulkMode,
        EntityModOptions.Update => WritesLinkedBinary(update, tenantId, ckCacheService),
        _ => false
    };

    private static bool WritesLinkedBinary(IEntityUpdateInfo<RtEntity> update, string tenantId,
        ICkCacheService ckCacheService)
    {
        if (update.RtEntity == null)
        {
            return false;
        }

        var ckTypeGraph = ckCacheService.GetRtCkType(tenantId, update.CkTypeId);
        return ckTypeGraph != null && ckTypeGraph.AllAttributes.Values.Any(a =>
            a.ValueType == AttributeValueTypesDto.BinaryLinked &&
            update.RtEntity.GetAttributeLinkedBinaryValueOrDefault(a.AttributeName) != null);
    }

    private static async Task<Dictionary<OctoObjectId, RtEntity>> ReadStoredAsync(IOctoSession session,
        IRepositoryDataSource repositoryDataSource, ICkCacheService ckCacheService, RtCkId<CkTypeId> ckTypeId,
        IReadOnlyList<OctoObjectId> ids)
    {
        var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, ckTypeId)
                          ?? throw RuntimeRepositoryException.RtCkTypeIdDoesNotExistInCache(ckTypeId);
        var collection = ((IMongoDbRepositoryDataSource)repositoryDataSource)
            .GetRtDatabaseCollection<RtEntity>(ckTypeGraph);
        var filter = Builders<RtEntity>.Filter.In(e => e.RtId, ids);
        var found = await collection.FindManyAsync(session, filter).ConfigureAwait(false);
        return found.ToDictionary(e => e.RtId);
    }

    /// <summary>The origin value an insert, update or replace writes; <paramref name="writesOrigin" /> says whether it writes one at all.</summary>
    private static object? WrittenOrigin(IEntityUpdateInfo<RtEntity> update, out bool writesOrigin)
    {
        writesOrigin = false;
        object? value = null;
        if (update.RtEntity != null)
        {
            foreach (var (key, attributeValue) in update.RtEntity.Attributes)
            {
                if (SyncOriginRefusals.IsOriginName(key))
                {
                    writesOrigin = true;
                    value = attributeValue;
                }
            }
        }

        if (WritesOriginItemOrSecret(update))
        {
            // A member path of the origin ("RtSyncOrigin.SourceTenantId"), an item update or a secret clear of the
            // origin is never a valid origin.
            writesOrigin = true;
            value = null;
        }

        return value;
    }

    private static bool WritesOriginItemOrSecret(IEntityUpdateInfo<RtEntity> update) =>
        (update.RtEntity?.Attributes.Keys ?? []).Any(SyncOriginRefusals.IsOriginMemberPath) ||
        (update.RecordArrayItemUpdates ?? []).Any(i => SyncOriginRefusals.IsOriginNameOrMemberPath(i.AttributeName)) ||
        (update.ClearSecretAttributes ?? []).Any(SyncOriginRefusals.IsOriginNameOrMemberPath);

    private static bool NamesSource(object? origin, string sourceTenantId) =>
        origin is RtTypeWithAttributes record && SameTenant(SyncOriginRefusals.SourceOfRecord(record), sourceTenantId);

    // The write filter compares the stored source exactly; so does this check, so both rules agree.
    private static bool SameTenant(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);

    private static OperationMessage Refusal(IEntityUpdateInfo<RtEntity> update, string? sourceTenantId, string reason)
    {
        var rtId = update.RtId?.ToString() ?? update.RtEntity?.RtId.ToString() ?? "new";
        return SyncOriginRefusals.Message(update.ModOption.ToString(), rtId, update.CkTypeId.FullName, sourceTenantId,
            reason);
    }
}
