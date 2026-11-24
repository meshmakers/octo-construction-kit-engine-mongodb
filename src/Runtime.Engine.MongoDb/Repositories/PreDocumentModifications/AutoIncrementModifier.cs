using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     Numbers the auto-increment attributes (<c>autoIncrementReference</c>) of NEW entities from their
///     <c>System/AutoIncrement</c> counter. The pre-document-modification pipeline runs on insert and on replace and
///     does not say which (AB#6209), so the modifier tells them apart by the stored state:
///     <list type="bullet">
///         <item>An entity that is not stored yet gets a number only for an attribute without a value; a supplied
///         value (ImportRt, restore, blueprint seed) is kept and does not advance the counter.</item>
///         <item>An entity that is already stored (replace, or a bulk import of an existing entity) is never
///         renumbered; when its payload has no value for the attribute, the stored number is carried over, so a
///         replace never clears it either.</item>
///     </list>
///     Every entity of a mixed batch is handled; an entity or type without auto-increment attributes is skipped.
///     <para>
///         Allocation (AB#9753) is atomic: one <c>findOneAndUpdate</c> with <c>$inc</c> per counter and batch takes a
///         block of consecutive numbers, and its filter holds the <c>End</c> bound, so two writers never get the same
///         number and a counter never passes <c>End</c>. The counter is neither read first nor replaced. The block is
///         handed out in batch order (the entity rule engine keeps the order of the input).
///     </para>
///     <para>
///         Transactions: the <c>$inc</c> runs in the caller's session. Inside a transaction it is part of it, so an
///         abort rolls the counter back together with the entities. A concurrent writer of the same counter makes the
///         server abort the transaction with the label <c>TransientTransactionError</c>; that exception is passed on
///         unchanged, because only the owner of the transaction can retry it (the whole transaction). Outside a
///         transaction the <c>$inc</c> is retried here (bounded) on that label or a write conflict; it is committed at
///         once, so a later failure of the write leaves a gap in the numbers.
///     </para>
/// </summary>
public class AutoIncrementModifier(
    ICkCacheService ckCacheService,
    ILogger<AutoIncrementModifier> logger)
    : IPreDocumentModification<RtEntity>
{
    /// <summary>Attempts of one allocation outside a transaction (the first one included).</summary>
    internal const int MaxAllocationAttempts = 3;

    private const string CurrentValueField = "attributes.currentValue";
    private const string EndField = "attributes.end";

    /// <summary>Test seam (AB#9753): runs before every allocation attempt, e.g. to inject a transient error.</summary>
    internal Func<Task>? BeforeAllocationAttemptAsync { get; init; }

    public async Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        var documentList = documents.ToArray();
        var autoIncrementTypes = new Dictionary<RtCkId<CkTypeId>, AutoIncrementType>();
        foreach (IGrouping<RtCkId<CkTypeId>, RtEntity> ckTypeGroup in documentList.GroupBy(d => d.GetRtCkTypeId()))
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, ckTypeGroup.Key);
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId, ckTypeGroup.Key);
            }

            var typeIncrements = ckTypeGraph.AllAttributes.Values
                .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference)).ToList();
            if (typeIncrements.Count == 0)
            {
                continue;
            }

            var attributes = new List<(CkTypeAttributeGraph Attribute, string Reference)>();
            foreach (var typeIncrement in typeIncrements)
            {
                if (!ckTypeGraph.AllAttributesByName.TryGetValue(typeIncrement.AttributeName,
                        out CkTypeAttributeGraph? ckTypeAttributeGraph))
                {
                    throw InvalidAttributeException.AttributeNotFoundAtRtCkIdType(ckTypeGroup.Key,
                        typeIncrement.AttributeName);
                }

                attributes.Add((ckTypeAttributeGraph, typeIncrement.AutoIncrementReference!));
            }

            // Only auto-increment types are read, in the session (transaction) of the write.
            var storedEntities = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource, ckTypeGraph,
                ckTypeGroup);
            autoIncrementTypes.Add(ckTypeGroup.Key, new AutoIncrementType(ckTypeGroup.Key, attributes, storedEntities));
        }

        if (autoIncrementTypes.Count == 0)
        {
            return;
        }

        // The numbers to allocate, in batch order (also across types that share a counter).
        var requests = new List<NumberRequest>();
        foreach (var rtEntity in documentList)
        {
            if (!autoIncrementTypes.TryGetValue(rtEntity.GetRtCkTypeId(), out var autoIncrementType))
            {
                continue;
            }

            autoIncrementType.StoredEntities.TryGetValue(rtEntity.RtId, out var storedEntity);
            foreach (var (ckTypeAttributeGraph, reference) in autoIncrementType.Attributes)
            {
                var attributeName = ckTypeAttributeGraph.AttributeName;
                if (HasValue(rtEntity.GetAttributeValueOrDefault(attributeName)))
                {
                    // A supplied value is kept: never renumber, never advance the counter for it.
                    continue;
                }

                if (storedEntity != null)
                {
                    // Already stored (replace): carry the existing number over instead of numbering again.
                    var storedValue = storedEntity.GetAttributeValueOrDefault(attributeName);
                    if (HasValue(storedValue))
                    {
                        rtEntity.SetAttributeValue(attributeName, ckTypeAttributeGraph.ValueType, storedValue);
                    }

                    continue;
                }

                requests.Add(new NumberRequest(rtEntity, ckTypeAttributeGraph, reference, autoIncrementType.CkTypeId));
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        // Looked up VERSION-LESS and only when an auto-increment attribute is actually written (CK v2 F1.0, D-G1-1).
        // The generated SystemCkIds.CkAutoIncrementTypeId carries the System version this service was compiled
        // against (e.g. System-2.5.0/AutoIncrement-1); since the embedded-import downgrade guard keeps a NEWER System
        // in a tenant, that versioned id is not in the tenant's CK cache, and the unconditional lookup made every
        // entity insert of an older service fail.
        var autoIncrementGraphType = ckCacheService.GetRtCkType(repositoryDataSource.TenantId,
            SystemCkIds.CkAutoIncrementTypeId.ToRtCkId());
        var autoIncrementCollection = repositoryDataSource.GetRtCollection<RtAutoIncrement>(autoIncrementGraphType);

        foreach (var counterRequests in requests.GroupBy(r => r.Reference))
        {
            var block = counterRequests.ToList();
            var (counter, firstNumber) = await AllocateAsync(session, autoIncrementCollection, counterRequests.Key,
                block.Count, block[0].CkTypeId);

            for (var i = 0; i < block.Count; i++)
            {
                var attribute = block[i].Attribute;
                var value = firstNumber + i;
                if (!string.IsNullOrEmpty(counter.Format) && attribute.ValueType == AttributeValueTypesDto.String)
                {
                    block[i].Entity.SetAttributeValue(attribute.AttributeName, attribute.ValueType,
                        string.Format(counter.Format, value));
                }
                else
                {
                    block[i].Entity.SetAttributeValue(attribute.AttributeName, attribute.ValueType, value);
                }
            }
        }
    }

    private static bool HasValue(object? value) =>
        value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

    /// <summary>
    ///     Takes <paramref name="count" /> consecutive numbers from the counter <paramref name="reference" /> with one
    ///     <c>findOneAndUpdate</c>: <c>$inc</c> by <paramref name="count" />, filtered on
    ///     <c>currentValue + count &lt;= end</c>. Returns the counter after the increment and the first number of the
    ///     block.
    /// </summary>
    private async Task<(RtAutoIncrement Counter, long FirstNumber)> AllocateAsync(IOctoSession session,
        IDataSourceCollection<OctoObjectId, RtAutoIncrement> autoIncrementCollection, string reference, int count,
        RtCkId<CkTypeId> requestingCkTypeId)
    {
        var mongoCollection = ((IMongoDbDataSourceCollection<OctoObjectId, RtAutoIncrement>)autoIncrementCollection)
            .GetMongoCollection();
        var octoSession = (IOctoSessionInternal)session;

        var filter = Builders<RtAutoIncrement>.Filter.And(
            Builders<RtAutoIncrement>.Filter.Eq(c => c.RtWellKnownName, reference),
            new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray
            {
                new BsonDocument("$add", new BsonArray { "$" + CurrentValueField, count }),
                "$" + EndField
            })));
        var update = Builders<RtAutoIncrement>.Update.Inc(CurrentValueField, (long)count);
        var options = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        RtAutoIncrement? counter;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                if (BeforeAllocationAttemptAsync != null)
                {
                    await BeforeAllocationAttemptAsync();
                }

                counter = await mongoCollection.FindOneAndUpdateAsync(octoSession.SessionHandle, filter, update,
                    options);
                break;
            }
            catch (Exception ex) when (!octoSession.IsTransactionActive && attempt < MaxAllocationAttempts &&
                                       RepositoryDistributedLockService.IsTransientTransactionError(ex))
            {
                // Outside a transaction the failed $inc did not apply, so retrying it hands out the block once.
                // Inside a transaction the server has aborted it: the exception goes to the owner unchanged.
                logger.LogWarning(
                    "Transient error while allocating {Count} number(s) from counter {Reference} (attempt {Attempt}/{MaxAttempts}), retrying",
                    count, reference, attempt, MaxAllocationAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        if (counter != null)
        {
            return (counter, counter.CurrentValue - count + 1);
        }

        // Nothing matched: either there is no such counter, or it cannot take the block without passing End.
        var existing = await autoIncrementCollection.FindManyAsync(session, c => c.RtWellKnownName == reference);
        var stored = existing.FirstOrDefault();
        if (stored == null)
        {
            throw InvalidAttributeException.AutoIncrementReferenceNotFound(requestingCkTypeId, reference);
        }

        throw AutoIncrementFailedException.AutoIncrementEndReached(stored.RtId, reference, stored.CurrentValue,
            stored.End, count);
    }

    private sealed record AutoIncrementType(
        RtCkId<CkTypeId> CkTypeId,
        IReadOnlyList<(CkTypeAttributeGraph Attribute, string Reference)> Attributes,
        IReadOnlyDictionary<OctoObjectId, RtEntity> StoredEntities);

    private sealed record NumberRequest(
        RtEntity Entity,
        CkTypeAttributeGraph Attribute,
        string Reference,
        RtCkId<CkTypeId> CkTypeId);
}
