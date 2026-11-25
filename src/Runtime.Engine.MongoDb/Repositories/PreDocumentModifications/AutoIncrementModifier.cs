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
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
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
///         Allocation (AB#9753) is atomic: one <c>findOneAndUpdate</c> per counter and batch takes a block of
///         consecutive numbers - from <c>max(counter, StartValue - 1) + 1</c> on, the floor of D5 (AB#9755), so a
///         start value continues an external range and one set below the counter is ignored - and its filter holds
///         the <c>End</c> bound, so two writers never get the same number and a counter never passes <c>End</c>. The counter is neither read first nor replaced. The block is
///         handed out in batch order (the entity rule engine keeps the order of the input).
///     </para>
///     <para>
///         Fiscal-year ranges (AB#9754, <c>PeriodKind = FiscalYear</c>): every entity's fiscal year is taken from its
///         creation time (the injected clock) read in the range's <c>ReferenceTimeZone</c>, and the counter of that
///         year in <c>Periods</c> is advanced - one atomic statement per counter, fiscal year and batch, so a batch
///         that spans the start of a fiscal year gives each entity the number of its own year. The first number of a
///         new fiscal year adds its period entry in the same atomic statement (a conditional update that matches only
///         while the year is missing), so two writers can never both create it; the loser of that race takes its
///         numbers from the entry the winner created. A range without periods (legacy) numbers exactly as before,
///         with one statement and no read.
///     </para>
///     <para>
///         Transactions: the statements run in the caller's session. Inside a transaction they are part of it, so an
///         abort rolls the counter back together with the entities. A concurrent writer of the same counter makes the
///         server abort the transaction with the label <c>TransientTransactionError</c>; that exception is passed on
///         unchanged, because only the owner of the transaction can retry it (the whole transaction, with the entities
///         built again from the input: the modifier writes the rolled-back number into the entity object). Outside a
///         transaction every statement is retried here (bounded) on that label or a write conflict; it is committed at
///         once, so a later failure of the write leaves a gap in the numbers.
///     </para>
/// </summary>
public class AutoIncrementModifier(
    ICkCacheService ckCacheService,
    ILogger<AutoIncrementModifier> logger,
    TimeProvider? timeProvider = null)
    : IPreDocumentModification<RtEntity>
{
    /// <summary>Attempts of one allocation statement outside a transaction (the first one included).</summary>
    internal const int MaxAllocationAttempts = 3;

    private const string CurrentValueField = "attributes.currentValue";
    private const string EndField = "attributes.end";
    private const string StartValueField = "attributes.startValue";
    private const string PeriodKindField = "attributes.periodKind";
    private const string PeriodsField = "attributes.periods";
    private const string PeriodFiscalYearMember = "attributes.fiscalYear";
    private const string PeriodCurrentValueMember = "attributes.currentValue";
    private const string PeriodIdentifier = "p";
    private const string PeriodAttributesMember = "attributes";
    private const string PeriodCurrentValueName = "currentValue";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

                // The creation time of this entity decides its fiscal year (AB#9754); read per entity, so a batch
                // that runs across the start of a fiscal year numbers each entity in its own year.
                requests.Add(new NumberRequest(rtEntity, ckTypeAttributeGraph, reference, autoIncrementType.CkTypeId,
                    _timeProvider.GetUtcNow()));
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
            await NumberAsync(session, autoIncrementCollection, counterRequests.Key, counterRequests.ToList());
        }
    }

    private static bool HasValue(object? value) =>
        value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

    /// <summary>Allocates and writes the numbers of all <paramref name="block" /> requests of one counter.</summary>
    private async Task NumberAsync(IOctoSession session,
        IDataSourceCollection<OctoObjectId, RtAutoIncrement> autoIncrementCollection, string reference,
        IReadOnlyList<NumberRequest> block)
    {
        var mongoCollection = ((IMongoDbDataSourceCollection<OctoObjectId, RtAutoIncrement>)autoIncrementCollection)
            .GetMongoCollection();
        var requestingCkTypeId = block[0].CkTypeId;

        // A range without periods: exactly the AB#9753 statement, no read.
        var counter = await AllocateWithoutPeriodsAsync(session, mongoCollection, reference, block.Count);
        if (counter != null)
        {
            // The range rules are checked after the statement (it reads nothing before). A range that is not valid -
            // e.g. a legacy format stored before System 2.9.0 that string.Format cannot format (AB#9755) - is refused
            // naming the range; the block it took stays unused. The counter is never set back: another request may
            // have taken the next block meanwhile, so a gap is accepted, a duplicate never.
            var settings = GetValidSettings(counter, reference);
            var firstNumber = ToLong(counter.CurrentValue) - block.Count + 1;
            for (var i = 0; i < block.Count; i++)
            {
                Assign(block[i], settings, firstNumber + i, null);
            }

            return;
        }

        // Nothing matched: there is no such counter, it cannot take the block without passing End, or it is a
        // fiscal-year range. Read it once to tell which.
        var stored = await FindCounterAsync(session, autoIncrementCollection, reference);
        if (stored == null)
        {
            throw InvalidAttributeException.AutoIncrementReferenceNotFound(requestingCkTypeId, reference);
        }

        var storedSettings = GetValidSettings(stored, reference);
        if (!storedSettings.HasFiscalYearPeriods)
        {
            throw AutoIncrementFailedException.AutoIncrementEndReached(stored.RtId, reference,
                ToLong(stored.CurrentValue), ToLong(stored.End), block.Count);
        }

        // One statement per fiscal year of the batch; the requests of a year keep their batch order.
        foreach (var yearRequests in block.GroupBy(r => storedSettings.FiscalYearOf(r.CreatedAt)))
        {
            var yearBlock = yearRequests.ToList();
            var firstNumber = await AllocateInPeriodAsync(session, autoIncrementCollection, mongoCollection, stored.RtId,
                reference, yearRequests.Key, yearBlock.Count, storedSettings.FirstNumberOfAPeriod,
                requestingCkTypeId);
            for (var i = 0; i < yearBlock.Count; i++)
            {
                Assign(yearBlock[i], storedSettings, firstNumber + i, yearRequests.Key);
            }
        }
    }

    /// <summary>
    ///     Writes number <paramref name="value" /> into the entity: formatted for a String attribute of a range with a
    ///     format, else the plain number (as before System 2.9.0).
    /// </summary>
    private static void Assign(NumberRequest request, AutoIncrementRangeSettings settings, long value, int? fiscalYear)
    {
        var attribute = request.Attribute;
        if (settings.Format.IsSet && attribute.ValueType == AttributeValueTypesDto.String)
        {
            request.Entity.SetAttributeValue(attribute.AttributeName, attribute.ValueType,
                settings.Format.Format(value, fiscalYear));
        }
        else
        {
            request.Entity.SetAttributeValue(attribute.AttributeName, attribute.ValueType, value);
        }
    }

    private static AutoIncrementRangeSettings GetValidSettings(RtAutoIncrement counter, string reference)
    {
        if (!AutoIncrementRangeSettings.TryCreate(counter, out var settings, out var error))
        {
            throw AutoIncrementFailedException.InvalidNumberRange(counter.RtId, reference, error!);
        }

        return settings;
    }

    private static async Task<RtAutoIncrement?> FindCounterAsync(IOctoSession session,
        IDataSourceCollection<OctoObjectId, RtAutoIncrement> autoIncrementCollection, string reference)
    {
        var existing = await autoIncrementCollection.FindManyAsync(session, c => c.RtWellKnownName == reference);
        return existing.FirstOrDefault();
    }

    /// <summary>
    ///     Takes <paramref name="count" /> consecutive numbers from the counter <paramref name="reference" /> with one
    ///     <c>findOneAndUpdate</c> on a range without fiscal-year periods: the counter becomes
    ///     <c>max(currentValue, StartValue - 1) + count</c> (the <c>StartValue</c> floor of D5, AB#9755; without a
    ///     start value exactly the increment of AB#9753), filtered on that value being <c>&lt;= end</c>. One pipeline
    ///     update, so the floor and the increment are one atomic statement. Returns the counter after the update, or
    ///     null when nothing matched.
    /// </summary>
    private Task<RtAutoIncrement?> AllocateWithoutPeriodsAsync(IOctoSession session,
        IMongoCollection<RtAutoIncrement> mongoCollection, string reference, int count)
    {
        var next = NextValue("$" + CurrentValueField, count);
        var filter = Builders<RtAutoIncrement>.Filter.And(
            Builders<RtAutoIncrement>.Filter.Eq(c => c.RtWellKnownName, reference),
            new BsonDocument(PeriodKindField, new BsonDocument("$ne", AutoIncrementRangeSettings.PeriodKindFiscalYear)),
            new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray { next, "$" + EndField })));
        var update = Builders<RtAutoIncrement>.Update.Pipeline(new BsonDocument[]
        {
            new("$set", new BsonDocument(CurrentValueField, next))
        });
        var options = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        return ExecuteAsync<RtAutoIncrement?>(session, reference, count, async sessionHandle =>
            await mongoCollection.FindOneAndUpdateAsync(sessionHandle, filter, update, options));
    }

    /// <summary>
    ///     The counter after taking <paramref name="count" /> numbers (aggregation expression):
    ///     <c>max(current, StartValue - 1) + count</c> (D5), as a long. A missing counter counts as 0, a missing start
    ///     value as 1; a stored start value that is not a number counts as absent here, and the range rules refuse it
    ///     right after the statement.
    /// </summary>
    private static BsonDocument NextValue(string currentValueExpression, int count) =>
        new("$add", new BsonArray
        {
            new BsonDocument("$max", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { currentValueExpression, 0L }),
                new BsonDocument("$subtract", new BsonArray
                {
                    new BsonDocument("$ifNull", new BsonArray
                    {
                        new BsonDocument("$convert", new BsonDocument
                        {
                            { "input", "$" + StartValueField }, { "to", "long" }, { "onError", BsonNull.Value },
                            { "onNull", BsonNull.Value }
                        }),
                        1L
                    }),
                    1L
                })
            }),
            (long)count
        });

    /// <summary>
    ///     Takes <paramref name="count" /> consecutive numbers from the period <paramref name="fiscalYear" /> of the
    ///     fiscal-year range <paramref name="counterRtId" /> and returns the first of them. The period counter is advanced
    ///     with one pipeline update of its <c>Periods</c> entry; a missing period is added together with its first
    ///     block by one conditional update that matches only while no entry of that year exists. When a parallel
    ///     writer added it first, the increment runs again on the entry it added. <c>End</c> bounds every period, and a
    ///     <c>StartValue</c> raised above the period's counter is its floor (D5).
    /// </summary>
    private async Task<long> AllocateInPeriodAsync(IOctoSession session,
        IDataSourceCollection<OctoObjectId, RtAutoIncrement> autoIncrementCollection,
        IMongoCollection<RtAutoIncrement> mongoCollection, OctoObjectId counterRtId, string reference, int fiscalYear,
        int count, long firstNumberOfAPeriod, RtCkId<CkTypeId> requestingCkTypeId)
    {
        var counterFilter = Builders<RtAutoIncrement>.Filter.And(
            Builders<RtAutoIncrement>.Filter.Eq(c => c.RtId, counterRtId),
            new BsonDocument(PeriodKindField, AutoIncrementRangeSettings.PeriodKindFiscalYear));

        // 1. The period exists and can take the block. Its counter becomes max(currentValue, StartValue - 1) + count
        //    (the StartValue floor of D5, AB#9755), in one pipeline update of the period array.
        var nextInPeriod = NextValue($"$${PeriodIdentifier}.{PeriodCurrentValueMember}", count);
        var isTheYear = new BsonDocument("$eq", new BsonArray
            { $"$${PeriodIdentifier}.{PeriodFiscalYearMember}", fiscalYear });
        var incrementFilter = Builders<RtAutoIncrement>.Filter.And(counterFilter,
            new BsonDocument("$expr", new BsonDocument("$anyElementTrue", new BsonArray
            {
                new BsonDocument("$map", new BsonDocument
                {
                    { "input", new BsonDocument("$ifNull", new BsonArray { "$" + PeriodsField, new BsonArray() }) },
                    { "as", PeriodIdentifier },
                    {
                        "in", new BsonDocument("$and", new BsonArray
                        {
                            isTheYear, new BsonDocument("$lte", new BsonArray { nextInPeriod, "$" + EndField })
                        })
                    }
                })
            })));
        var increment = Builders<RtAutoIncrement>.Update.Pipeline(new BsonDocument[]
        {
            new("$set", new BsonDocument(PeriodsField, new BsonDocument("$map", new BsonDocument
            {
                { "input", "$" + PeriodsField },
                { "as", PeriodIdentifier },
                {
                    "in", new BsonDocument("$cond", new BsonArray
                    {
                        isTheYear,
                        new BsonDocument("$mergeObjects", new BsonArray
                        {
                            $"$${PeriodIdentifier}",
                            new BsonDocument(PeriodAttributesMember, new BsonDocument("$mergeObjects", new BsonArray
                            {
                                $"$${PeriodIdentifier}.{PeriodAttributesMember}",
                                new BsonDocument(PeriodCurrentValueName, nextInPeriod)
                            }))
                        }),
                        $"$${PeriodIdentifier}"
                    })
                }
            })))
        });
        var incrementOptions = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        // 2. The period does not exist yet: add it with its first block, only while it is missing.
        var lastNumberOfTheBlock = firstNumberOfAPeriod + count - 1;
        var createFilter = Builders<RtAutoIncrement>.Filter.And(counterFilter,
            new BsonDocument($"{PeriodsField}.{PeriodFiscalYearMember}", new BsonDocument("$ne", fiscalYear)),
            new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray
                { lastNumberOfTheBlock, "$" + EndField })));
        var newPeriod = NewPeriodDocument(fiscalYear, lastNumberOfTheBlock);
        var create = Builders<RtAutoIncrement>.Update.Pipeline(new BsonDocument[]
        {
            new("$set", new BsonDocument(PeriodsField, new BsonDocument("$concatArrays", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$" + PeriodsField, new BsonArray() }),
                new BsonArray { new BsonDocument("$literal", newPeriod) }
            })))
        });
        var createOptions = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        for (var round = 1; round <= 2; round++)
        {
            var incremented = await ExecuteAsync<RtAutoIncrement?>(session, reference, count, async sessionHandle =>
                await mongoCollection.FindOneAndUpdateAsync(sessionHandle, incrementFilter, increment, incrementOptions));
            if (incremented != null)
            {
                return PeriodCurrentValue(incremented, fiscalYear) - count + 1;
            }

            if (round == 2)
            {
                break;
            }

            var created = await ExecuteAsync<RtAutoIncrement?>(session, reference, count, async sessionHandle =>
                await mongoCollection.FindOneAndUpdateAsync(sessionHandle, createFilter, create, createOptions));
            if (created != null)
            {
                return firstNumberOfAPeriod;
            }

            // Not created: a parallel writer added the year first, or the block passes End. Increment again.
        }

        // Neither matched: tell the caller why.
        var stored = await FindCounterAsync(session, autoIncrementCollection, reference);
        if (stored == null)
        {
            throw InvalidAttributeException.AutoIncrementReferenceNotFound(requestingCkTypeId, reference);
        }

        if (!AutoIncrementRangeSettings.TryCreate(stored, out var settings, out var error))
        {
            throw AutoIncrementFailedException.InvalidNumberRange(stored.RtId, reference, error!);
        }

        if (!settings.HasFiscalYearPeriods)
        {
            throw AutoIncrementFailedException.InvalidNumberRange(stored.RtId, reference,
                "PeriodKind was changed while numbers were allocated; write again.");
        }

        var current = TryGetPeriodCurrentValue(stored, fiscalYear) ?? firstNumberOfAPeriod - 1;
        throw AutoIncrementFailedException.AutoIncrementEndReached(stored.RtId, reference, fiscalYear, current,
            ToLong(stored.End), count);
    }

    /// <summary>
    ///     Runs one allocation statement. Outside a transaction a failed statement did not apply, so it is retried
    ///     (bounded) on a transient error and hands out its block once. Inside a transaction the server has aborted it:
    ///     the exception goes to the owner unchanged.
    /// </summary>
    private async Task<TResult> ExecuteAsync<TResult>(IOctoSession session, string reference, int count,
        Func<IClientSessionHandle, Task<TResult>> statement)
    {
        var octoSession = (IOctoSessionInternal)session;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                if (BeforeAllocationAttemptAsync != null)
                {
                    await BeforeAllocationAttemptAsync();
                }

                return await statement(octoSession.SessionHandle);
            }
            catch (Exception ex) when (!octoSession.IsTransactionActive && attempt < MaxAllocationAttempts &&
                                       RepositoryDistributedLockService.IsTransientTransactionError(ex))
            {
                logger.LogWarning(
                    "Transient error while allocating {Count} number(s) from counter {Reference} (attempt {Attempt}/{MaxAttempts}), retrying",
                    count, reference, attempt, MaxAllocationAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    /// <summary>
    ///     The stored shape of a <c>System/AutoIncrementPeriod</c> record, written by the repository's own serializer
    ///     (<c>{ ckRecordId, attributes: { fiscalYear, currentValue } }</c>), so the counter reads back as written.
    /// </summary>
    private static BsonDocument NewPeriodDocument(int fiscalYear, long currentValue)
    {
        var period = new RtAutoIncrementPeriodRecord();
        period.SetAttributeValue(nameof(RtAutoIncrementPeriodRecord.FiscalYear), AttributeValueTypesDto.Int,
            fiscalYear);
        period.SetAttributeValue(nameof(RtAutoIncrementPeriodRecord.CurrentValue), AttributeValueTypesDto.Int,
            currentValue);

        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        BsonSerializer.LookupSerializer<RtRecord>().Serialize(BsonSerializationContext.CreateRoot(writer), period);
        return document;
    }

    private static long PeriodCurrentValue(RtAutoIncrement counter, int fiscalYear) =>
        TryGetPeriodCurrentValue(counter, fiscalYear) ??
        throw new InvalidOperationException(
            $"Counter '{counter.RtId}' has no period {fiscalYear} after it was incremented.");

    private static long? TryGetPeriodCurrentValue(RtAutoIncrement counter, int fiscalYear)
    {
        var period = counter.Periods?.FirstOrDefault(p => ToLong(p.FiscalYear) == fiscalYear);
        return period == null ? null : ToLong(period.CurrentValue);
    }

    private static long ToLong(object? value) => Convert.ToInt64(value ?? 0L);

    private sealed record AutoIncrementType(
        RtCkId<CkTypeId> CkTypeId,
        IReadOnlyList<(CkTypeAttributeGraph Attribute, string Reference)> Attributes,
        IReadOnlyDictionary<OctoObjectId, RtEntity> StoredEntities);

    private sealed record NumberRequest(
        RtEntity Entity,
        CkTypeAttributeGraph Attribute,
        string Reference,
        RtCkId<CkTypeId> CkTypeId,
        DateTimeOffset CreatedAt);
}
