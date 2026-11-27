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
///         Numbering on release (AB#9758, design note D8): a range with a release configuration
///         (<c>AutoIncrement.Release</c>) numbers a document only when it is in one of the <c>ReleasedValues</c> of its
///         <c>StateAttribute</c>: a draft is inserted without a number and the counter stays unchanged, so deleting a
///         draft leaves no gap. The number is taken by the write that moves the document into a released state - an
///         insert directly in that state, a replace, or a partial update (<see cref="ModifyUpdatesAsync" />, the
///         engine's <see cref="IPreDocumentUpdateModification{TDocument}" /> hook) that sets the state attribute -
///         and is written by that same write. A document that already has a number keeps it, so a released document
///         updated again neither changes its number nor advances the counter. A range without a release configuration
///         numbers on insert exactly as before, with one statement and no read.
///     </para>
///     <para>
///         Allocation (AB#9753) is atomic: one <c>findOneAndUpdate</c> per counter and batch takes a block of
///         consecutive numbers - from <c>max(counter, StartValue - 1) + 1</c> on, the floor of D5 (AB#9755), so a
///         start value continues an external range and one set below the counter is ignored - and its filter holds
///         the <c>End</c> bound, so two writers never get the same number and a counter never passes <c>End</c>. The counter is neither read first nor replaced. The block is
///         handed out in batch order (the entity rule engine keeps the order of the input).
///     </para>
///     <para>
///         Fiscal-year ranges (AB#9754, <c>PeriodKind = FiscalYear</c>): every entity's fiscal year is taken from the
///         time it is numbered (the injected clock) read in the range's <c>ReferenceTimeZone</c> - its creation time on a
///         range without release configuration, its release time on a range with one (D2) -, and the counter of that
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
    : IPreDocumentModification<RtEntity>, IPreDocumentUpdateModification<RtEntity>
{
    /// <summary>Attempts of one allocation statement outside a transaction (the first one included).</summary>
    internal const int MaxAllocationAttempts = 3;

    private const string CurrentValueField = "attributes.currentValue";
    private const string EndField = "attributes.end";
    private const string StartValueField = "attributes.startValue";
    private const string PeriodKindField = "attributes.periodKind";
    private const string PeriodsField = "attributes.periods";
    private const string ReleaseField = "attributes.release";
    private const string RtVersionField = "rtVersion";
    private const string RtChangedDateTimeField = "rtChangedDateTime";
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
        var autoIncrementTypes = GetAutoIncrementTypes(repositoryDataSource, documentList);
        if (autoIncrementTypes.Count == 0)
        {
            return;
        }

        // Only auto-increment types are read, in the session (transaction) of the write.
        var storedEntities = new Dictionary<RtCkId<CkTypeId>, IReadOnlyDictionary<OctoObjectId, RtEntity>>();
        foreach (var (ckTypeId, autoIncrementType) in autoIncrementTypes)
        {
            storedEntities[ckTypeId] = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource,
                autoIncrementType.CkTypeGraph, documentList.Where(d => d.GetRtCkTypeId() == ckTypeId));
        }

        // The numbers to allocate, in batch order (also across types that share a counter).
        var requests = new List<NumberRequest>();
        foreach (var rtEntity in documentList)
        {
            if (!autoIncrementTypes.TryGetValue(rtEntity.GetRtCkTypeId(), out var autoIncrementType))
            {
                continue;
            }

            storedEntities[autoIncrementType.CkTypeId].TryGetValue(rtEntity.RtId, out var storedEntity);
            DateTimeOffset? numberedAt = null;
            foreach (var (ckTypeAttributeGraph, reference) in autoIncrementType.Attributes)
            {
                var attributeName = ckTypeAttributeGraph.AttributeName;
                if (HasValue(rtEntity.GetAttributeValueOrDefault(attributeName)))
                {
                    // A supplied value is kept: never renumber, never advance the counter for it.
                    continue;
                }

                var kind = NumberRequestKind.New;
                if (storedEntity != null)
                {
                    // Already stored (replace): carry the existing number over instead of numbering again.
                    var storedValue = storedEntity.GetAttributeValueOrDefault(attributeName);
                    if (HasValue(storedValue))
                    {
                        rtEntity.SetAttributeValue(attributeName, ckTypeAttributeGraph.ValueType, storedValue);
                        continue;
                    }

                    // Stored without a number (a draft of a range with release configuration): numbered only if the
                    // replace releases it (AB#9758); a range without release configuration leaves it as it is.
                    kind = NumberRequestKind.Stored;
                }

                // The time of this write decides the fiscal year (AB#9754, D2); read once per entity (and only when it
                // needs a number), so a batch that runs across the start of a fiscal year numbers each entity in its
                // own year, and all numbers of one entity are in the same year.
                numberedAt ??= _timeProvider.GetUtcNow();
                requests.Add(new NumberRequest(rtEntity, ckTypeAttributeGraph, reference, autoIncrementType.CkTypeGraph,
                    kind, numberedAt.Value));
            }
        }

        await NumberRequestsAsync(session, repositoryDataSource, requests, null);
    }

    /// <summary>
    ///     Numbers the documents a partial update releases (AB#9758): an update that sets the <c>StateAttribute</c> of a
    ///     range with release configuration to one of its <c>ReleasedValues</c> gives a document that has no number yet
    ///     the next number of the range, written by the same update statement. An update that does not set the state
    ///     attribute, that leaves the document in a draft state, or of a document that already has a number takes no
    ///     number; ranges without release configuration are never numbered by an update (as before).
    /// </summary>
    public async Task ModifyUpdatesAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IReadOnlyList<RtEntity> partialDocuments)
    {
        var autoIncrementTypes = GetAutoIncrementTypes(repositoryDataSource, partialDocuments);
        if (autoIncrementTypes.Count == 0)
        {
            return;
        }

        var candidates = new List<UpdateCandidate>();
        foreach (var partial in partialDocuments)
        {
            if (!autoIncrementTypes.TryGetValue(partial.GetRtCkTypeId(), out var autoIncrementType))
            {
                continue;
            }

            foreach (var (attribute, reference) in autoIncrementType.Attributes)
            {
                if (!HasValue(partial.GetAttributeValueOrDefault(attribute.AttributeName)))
                {
                    candidates.Add(new UpdateCandidate(partial, autoIncrementType, attribute, reference));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        // The ranges are read once per batch; only a range with release configuration numbers on an update.
        var autoIncrementCollection = GetAutoIncrementCollection(repositoryDataSource);
        var references = candidates.Select(c => c.Reference).Distinct().ToList();
        var counters = (await autoIncrementCollection.FindManyAsync(session,
                c => references.Contains(c.RtWellKnownName!)))
            .GroupBy(c => c.RtWellKnownName!).ToDictionary(g => g.Key, g => g.First());

        var releasing = new List<UpdateCandidate>();
        foreach (var candidate in candidates)
        {
            if (!counters.TryGetValue(candidate.Reference, out var counter))
            {
                continue;
            }

            var release = GetValidRelease(counter, candidate.Reference);
            if (release == null)
            {
                continue;
            }

            var stateAttribute = GetStateAttribute(release, candidate.Type.CkTypeGraph, counter, candidate.Reference);
            if (!candidate.Partial.Attributes.ContainsKey(stateAttribute.AttributeName))
            {
                // The update does not change the state: no transition, no number.
                continue;
            }

            if (IsReleased(repositoryDataSource, release, stateAttribute, candidate.Partial))
            {
                releasing.Add(candidate);
            }
        }

        if (releasing.Count == 0)
        {
            return;
        }

        // A document that already has a number keeps it (a released document updated again).
        var requests = new List<NumberRequest>();
        var numberedAt = new Dictionary<RtEntity, DateTimeOffset>(ReferenceEqualityComparer.Instance);
        foreach (var typeGroup in releasing.GroupBy(c => c.Type.CkTypeId))
        {
            var stored = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource,
                typeGroup.First().Type.CkTypeGraph, typeGroup.Select(c => c.Partial));
            foreach (var candidate in typeGroup)
            {
                if (!stored.TryGetValue(candidate.Partial.RtId, out var storedEntity) ||
                    HasValue(storedEntity.GetAttributeValueOrDefault(candidate.Attribute.AttributeName)))
                {
                    continue;
                }

                if (!numberedAt.TryGetValue(candidate.Partial, out var at))
                {
                    at = _timeProvider.GetUtcNow();
                    numberedAt[candidate.Partial] = at;
                }

                requests.Add(new NumberRequest(candidate.Partial, candidate.Attribute, candidate.Reference,
                    candidate.Type.CkTypeGraph, NumberRequestKind.Stored, at));
            }
        }

        // Back in batch order for the allocation.
        var order = new Dictionary<RtEntity, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < partialDocuments.Count; i++)
        {
            order.TryAdd(partialDocuments[i], i);
        }

        requests = requests.OrderBy(r => order[r.Entity]).ToList();

        await NumberRequestsAsync(session, repositoryDataSource, requests, counters);
    }

    /// <summary>The CK types of <paramref name="documents" /> that have auto-increment attributes.</summary>
    private Dictionary<RtCkId<CkTypeId>, AutoIncrementType> GetAutoIncrementTypes(
        IRepositoryDataSource repositoryDataSource, IEnumerable<RtEntity> documents)
    {
        var autoIncrementTypes = new Dictionary<RtCkId<CkTypeId>, AutoIncrementType>();
        foreach (var ckTypeId in documents.Select(d => d.GetRtCkTypeId()).Distinct())
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, ckTypeId);
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId, ckTypeId);
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
                    throw InvalidAttributeException.AttributeNotFoundAtRtCkIdType(ckTypeId,
                        typeIncrement.AttributeName);
                }

                attributes.Add((ckTypeAttributeGraph, typeIncrement.AutoIncrementReference!));
            }

            autoIncrementTypes.Add(ckTypeId, new AutoIncrementType(ckTypeId, ckTypeGraph, attributes));
        }

        return autoIncrementTypes;
    }

    /// <summary>
    ///     Allocates and writes the numbers of <paramref name="requests" />, one counter after the other.
    ///     <paramref name="counters" /> are counters the caller has read already (by reference), or null.
    /// </summary>
    private async Task NumberRequestsAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        List<NumberRequest> requests, IReadOnlyDictionary<string, RtAutoIncrement>? counters)
    {
        if (requests.Count == 0)
        {
            return;
        }

        var autoIncrementCollection = GetAutoIncrementCollection(repositoryDataSource);
        foreach (var counterRequests in requests.GroupBy(r => r.Reference))
        {
            RtAutoIncrement? counter = null;
            counters?.TryGetValue(counterRequests.Key, out counter);
            await NumberAsync(session, repositoryDataSource, autoIncrementCollection, counterRequests.Key,
                counterRequests.ToList(), counter);
        }
    }

    /// <summary>
    ///     The <c>System/AutoIncrement</c> collection. Looked up VERSION-LESS and only when an auto-increment attribute
    ///     is actually written (CK v2 F1.0, D-G1-1): the generated SystemCkIds.CkAutoIncrementTypeId carries the System
    ///     version this service was compiled against (e.g. System-2.5.0/AutoIncrement-1); since the embedded-import
    ///     downgrade guard keeps a NEWER System in a tenant, that versioned id is not in the tenant's CK cache, and the
    ///     unconditional lookup made every entity insert of an older service fail.
    /// </summary>
    private IDataSourceCollection<OctoObjectId, RtAutoIncrement> GetAutoIncrementCollection(
        IRepositoryDataSource repositoryDataSource)
    {
        var autoIncrementGraphType = ckCacheService.GetRtCkType(repositoryDataSource.TenantId,
            SystemCkIds.CkAutoIncrementTypeId.ToRtCkId());
        return repositoryDataSource.GetRtCollection<RtAutoIncrement>(autoIncrementGraphType);
    }

    private static bool HasValue(object? value) =>
        value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

    /// <summary>
    ///     Allocates and writes the numbers of the requests of one counter. A block of only new entities first tries the
    ///     one statement of a range without periods and without release configuration (no read, exactly as before
    ///     AB#9758); otherwise the range is read once (or taken from <paramref name="counter" />) to decide which
    ///     requests take a number: on a range with release configuration those whose document is released, on a range
    ///     without it only the new entities.
    /// </summary>
    private async Task NumberAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IDataSourceCollection<OctoObjectId, RtAutoIncrement> autoIncrementCollection, string reference,
        IReadOnlyList<NumberRequest> requests, RtAutoIncrement? counter)
    {
        var mongoCollection = ((IMongoDbDataSourceCollection<OctoObjectId, RtAutoIncrement>)autoIncrementCollection)
            .GetMongoCollection();
        var requestingCkTypeId = requests[0].Entity.GetRtCkTypeId();

        if (counter == null && requests.All(r => r.Kind == NumberRequestKind.New))
        {
            // A range without periods and without release configuration: exactly the AB#9753 statement, no read.
            var allocated = await AllocateWithoutPeriodsAsync(session, mongoCollection, reference, requests.Count,
                withoutRelease: true);
            if (allocated != null)
            {
                // The range rules are checked after the statement (it reads nothing before). A range that is not
                // valid - e.g. a legacy format stored before System 2.9.0 that string.Format cannot format (AB#9755) -
                // is refused naming the range; the block it took stays unused. The counter is never set back: another
                // request may have taken the next block meanwhile, so a gap is accepted, a duplicate never.
                AssignBlock(requests, GetValidSettings(allocated, reference), allocated);
                return;
            }
        }

        // Nothing matched (there is no such counter, it cannot take the block without passing End, it is a
        // fiscal-year range or it has a release configuration), or the block holds stored documents. Read it once.
        var stored = counter ?? await FindCounterAsync(session, autoIncrementCollection, reference);
        if (stored == null)
        {
            if (requests.Any(r => r.Kind == NumberRequestKind.New))
            {
                throw InvalidAttributeException.AutoIncrementReferenceNotFound(requestingCkTypeId, reference);
            }

            // Only stored documents without a number: as before AB#9758 they need no counter.
            return;
        }

        var release = GetValidRelease(stored, reference);
        var block = release == null
            ? requests.Where(r => r.Kind == NumberRequestKind.New).ToList()
            : requests.Where(r => IsReleased(repositoryDataSource, release,
                GetStateAttribute(release, r.CkTypeGraph, stored, reference), r.Entity)).ToList();
        if (block.Count == 0)
        {
            // Drafts only (or stored documents of a range without release configuration): no number, the counter
            // stays unchanged.
            return;
        }

        var storedSettings = GetValidSettings(stored, reference);
        if (!storedSettings.HasFiscalYearPeriods)
        {
            // A range without periods. Without release configuration the first statement matched nothing although the
            // range has no periods: it was a fiscal-year range when the statement ran and was switched meanwhile, or
            // the block passes End. Try (once more) against what is stored now; only a block that still does not fit
            // is reported as end reached.
            var allocated = await AllocateWithoutPeriodsAsync(session, mongoCollection, reference, block.Count,
                withoutRelease: false);
            if (allocated != null)
            {
                AssignBlock(block, GetValidSettings(allocated, reference), allocated);
                return;
            }

            stored = await FindCounterAsync(session, autoIncrementCollection, reference) ?? stored;
            throw AutoIncrementFailedException.AutoIncrementEndReached(stored.RtId, reference,
                ToLong(stored.CurrentValue), ToLong(stored.End), block.Count, storedSettings.StartValue);
        }

        // A fiscal-year number is the formatted text with the year; a plain (Int) attribute would get the bare period
        // counter, which repeats every fiscal year. Refused before anything is allocated, so no gap either.
        var plainAttribute = block.FirstOrDefault(r => r.Attribute.ValueType != AttributeValueTypesDto.String);
        if (plainAttribute != null)
        {
            throw AutoIncrementFailedException.InvalidNumberRange(stored.RtId, reference,
                $"PeriodKind FiscalYear numbers only String attributes; attribute '{plainAttribute.Attribute.AttributeName}' " +
                $"of '{plainAttribute.Entity.GetRtCkTypeId()}' is {plainAttribute.Attribute.ValueType}, and its numbers " +
                "would repeat every fiscal year.");
        }

        // One statement per fiscal year of the batch; the requests of a year keep their batch order.
        foreach (var yearRequests in block.GroupBy(r => storedSettings.FiscalYearOf(r.NumberedAt)))
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

    /// <summary>Writes the block a range without periods handed out (it ended at <paramref name="counter" />).</summary>
    private static void AssignBlock(IReadOnlyList<NumberRequest> block, AutoIncrementRangeSettings settings,
        RtAutoIncrement counter)
    {
        var firstNumber = FirstNumberOfTheBlock(ToLong(counter.CurrentValue), block.Count);
        for (var i = 0; i < block.Count; i++)
        {
            Assign(block[i], settings, firstNumber + i, null);
        }
    }

    /// <summary>The release configuration of the range, or null when it has none; refused when it is not valid.</summary>
    private static NumberRangeRelease? GetValidRelease(RtAutoIncrement counter, string reference)
    {
        if (!NumberRangeRelease.TryCreate(counter, out var release, out var error))
        {
            throw AutoIncrementFailedException.InvalidNumberRange(counter.RtId, reference, error!);
        }

        return release;
    }

    /// <summary>The state attribute of the release configuration on the document type; refused when it has none.</summary>
    private static CkTypeAttributeGraph GetStateAttribute(NumberRangeRelease release, CkTypeGraph ckTypeGraph,
        RtAutoIncrement counter, string reference)
    {
        if (!ckTypeGraph.AllAttributesByName.TryGetValue(release.StateAttribute, out var stateAttribute))
        {
            throw AutoIncrementFailedException.InvalidNumberRange(counter.RtId, reference,
                $"Release.StateAttribute '{release.StateAttribute}' is not an attribute of '{ckTypeGraph.CkTypeId}', " +
                "so its documents could never be released.");
        }

        return stateAttribute;
    }

    /// <summary>Whether the state <paramref name="document" /> is written with is a released one (D8).</summary>
    private bool IsReleased(IRepositoryDataSource repositoryDataSource, NumberRangeRelease release,
        CkTypeAttributeGraph stateAttribute, RtEntity document)
    {
        CkEnumGraph? stateEnum = null;
        if (stateAttribute.ValueCkEnumId != null)
        {
            ckCacheService.TryGetCkEnum(repositoryDataSource.TenantId, stateAttribute.ValueCkEnumId, out stateEnum);
        }

        return release.IsReleased(document.GetAttributeValueOrDefault(stateAttribute.AttributeName), stateAttribute,
            stateEnum);
    }

    private sealed record UpdateCandidate(
        RtEntity Partial,
        AutoIncrementType Type,
        CkTypeAttributeGraph Attribute,
        string Reference);

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
        IMongoCollection<RtAutoIncrement> mongoCollection, string reference, int count, bool withoutRelease)
    {
        var next = NextValue("$" + CurrentValueField, count);
        var filters = new List<FilterDefinition<RtAutoIncrement>>
        {
            Builders<RtAutoIncrement>.Filter.Eq(c => c.RtWellKnownName, reference),
            new BsonDocument(PeriodKindField, new BsonDocument("$ne", AutoIncrementRangeSettings.PeriodKindFiscalYear)),
            new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray { next, "$" + EndField }))
        };
        if (withoutRelease)
        {
            // AB#9758: a range with release configuration numbers only released documents; the caller reads it first.
            filters.Add(new BsonDocument(ReleaseField, BsonNull.Value));
        }

        var filter = Builders<RtAutoIncrement>.Filter.And(filters);
        var update = Builders<RtAutoIncrement>.Update.Pipeline(new BsonDocument[]
        {
            new("$set", WithVersionBump(new BsonDocument(CurrentValueField, next)))
        });
        var options = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        return ExecuteAsync<RtAutoIncrement?>(session, reference, count, async sessionHandle =>
            await mongoCollection.FindOneAndUpdateAsync(sessionHandle, filter, update, options));
    }

    /// <summary>
    ///     Adds to the <c>$set</c> stage of an allocation statement what every engine update writes: <c>rtVersion</c>
    ///     + 1 and <c>rtChangedDateTime</c>. Without the version bump a client that read the counter and writes it back
    ///     with an expected <c>rtVersion</c> (outside a transaction) would pass the version check after numbers were
    ///     taken and set the counter back, so those numbers would be issued again.
    /// </summary>
    private static BsonDocument WithVersionBump(BsonDocument set)
    {
        set.Add(RtVersionField, new BsonDocument("$add", new BsonArray
        {
            new BsonDocument("$ifNull", new BsonArray { "$" + RtVersionField, 0L }), 1L
        }));
        set.Add(RtChangedDateTimeField, "$$NOW");
        return set;
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
            new("$set", WithVersionBump(new BsonDocument(PeriodsField, new BsonDocument("$map", new BsonDocument
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
            }))))
        });
        var incrementOptions = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        // 2. The period does not exist yet: add it with its first block, only while it is missing. A block whose last
        //    number would pass long.MaxValue cannot fit below any End, so it is never created (no overflow).
        var blockFitsInALong = BlockFitsInALong(firstNumberOfAPeriod, count);
        var lastNumberOfTheBlock = blockFitsInALong ? firstNumberOfAPeriod + count - 1 : long.MaxValue;
        var createFilter = Builders<RtAutoIncrement>.Filter.And(counterFilter,
            new BsonDocument($"{PeriodsField}.{PeriodFiscalYearMember}", new BsonDocument("$ne", fiscalYear)),
            new BsonDocument("$expr", new BsonDocument("$lte", new BsonArray
                { lastNumberOfTheBlock, "$" + EndField })));
        var newPeriod = NewPeriodDocument(fiscalYear, lastNumberOfTheBlock);
        var create = Builders<RtAutoIncrement>.Update.Pipeline(new BsonDocument[]
        {
            new("$set", WithVersionBump(new BsonDocument(PeriodsField, new BsonDocument("$concatArrays", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$" + PeriodsField, new BsonArray() }),
                new BsonArray { new BsonDocument("$literal", newPeriod) }
            }))))
        });
        var createOptions = new FindOneAndUpdateOptions<RtAutoIncrement> { ReturnDocument = ReturnDocument.After };

        for (var round = 1; round <= 2; round++)
        {
            var incremented = await ExecuteAsync<RtAutoIncrement?>(session, reference, count, async sessionHandle =>
                await mongoCollection.FindOneAndUpdateAsync(sessionHandle, incrementFilter, increment, incrementOptions));
            if (incremented != null)
            {
                return FirstNumberOfTheBlock(PeriodCurrentValue(incremented, fiscalYear), count);
            }

            if (round == 2)
            {
                break;
            }

            var created = !blockFitsInALong
                ? null
                : await ExecuteAsync<RtAutoIncrement?>(session, reference, count, async sessionHandle =>
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
            ToLong(stored.End), count, settings.StartValue);
    }

    /// <summary>
    ///     Runs one allocation statement. Outside a transaction a failed statement did not apply, so it is retried
    ///     (bounded) on a transient error and hands out its block once. Inside a transaction the server has aborted it:
    ///     the exception goes to the owner unchanged.
    /// </summary>
    /// <summary>
    ///     Whether a failed allocation statement runs again: only outside a transaction (there it did not apply), only
    ///     on a transient error, and at most <see cref="MaxAllocationAttempts" /> attempts in all. Inside a transaction
    ///     the server has aborted it, so only the transaction owner can run it again.
    /// </summary>
    internal static bool ShouldRetryAllocation(Exception exception, bool inTransaction, int attempt) =>
        !inTransaction && attempt < MaxAllocationAttempts &&
        RepositoryDistributedLockService.IsTransientTransactionError(exception);

    /// <summary>The first number of a block of <paramref name="count" /> that ended the counter at <paramref name="counterAfter" />.</summary>
    internal static long FirstNumberOfTheBlock(long counterAfter, int count) => counterAfter - count + 1;

    /// <summary>True when the last number of a block starting at <paramref name="first" /> fits in a long.</summary>
    internal static bool BlockFitsInALong(long first, int count) => count - 1 <= long.MaxValue - first;

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
            catch (Exception ex) when (ShouldRetryAllocation(ex, octoSession.IsTransactionActive, attempt))
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
        CkTypeGraph CkTypeGraph,
        IReadOnlyList<(CkTypeAttributeGraph Attribute, string Reference)> Attributes);

    /// <summary>Whether a request is for an entity that is not stored yet, or for a stored one without a number.</summary>
    private enum NumberRequestKind
    {
        /// <summary>Not stored yet (insert): numbered on every range, on a release range only when released.</summary>
        New,

        /// <summary>Stored without a number (replace or update): numbered only by a release range that releases it.</summary>
        Stored
    }

    private sealed record NumberRequest(
        RtEntity Entity,
        CkTypeAttributeGraph Attribute,
        string Reference,
        CkTypeGraph CkTypeGraph,
        NumberRequestKind Kind,
        DateTimeOffset NumberedAt);
}
