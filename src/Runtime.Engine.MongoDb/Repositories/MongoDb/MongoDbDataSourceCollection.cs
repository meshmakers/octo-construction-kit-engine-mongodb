using System.Linq.Expressions;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class MongoDbDataSourceCollection<TKey, TDocument> : IMongoDbDataSourceCollection<TKey, TDocument>
    where TDocument : class, new()
    where TKey : notnull
{
    // ReSharper disable once StaticMemberInGenericType
    private readonly IMongoCollection<TDocument> _documentCollection;
    private readonly ILogger<MongoDbDataSourceCollection<TKey, TDocument>> _logger;
    private readonly IMongoDataSourceMapper<TKey, TDocument> _mongoDataSourceMapper;
    private readonly IMongoDocumentReadNormalizer<TDocument>? _readNormalizer;

    internal MongoDbDataSourceCollection(ILogger<MongoDbDataSourceCollection<TKey, TDocument>> logger,
        IMongoCollection<TDocument> documentCollection,
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper)
    {
        _logger = logger;
        _mongoDataSourceMapper = mongoDataSourceMapper;
        _documentCollection = documentCollection;
        _readNormalizer = mongoDataSourceMapper as IMongoDocumentReadNormalizer<TDocument>;
    }

    /// <summary>
    ///     AB#5533: hands a document read from the collection to the mapper's read normaliser (legacy
    ///     strings in Secret slots become <c>RtSecretValue.LegacyPlaintext</c>).
    /// </summary>
    private TDocument? NormalizeRead(TDocument? document)
    {
        if (document != null && _readNormalizer != null)
        {
            _readNormalizer.NormalizeAfterRead([document]);
        }

        return document;
    }

    /// <summary>
    ///     AB#5533: normalises every document of one read operation.
    /// </summary>
    private TList NormalizeRead<TList>(TList documents) where TList : IEnumerable<TDocument>
    {
        _readNormalizer?.NormalizeAfterRead(documents);
        return documents;
    }

    public IMongoDataSourceMapper<TKey, TDocument> MongoDataSourceMapper => _mongoDataSourceMapper;

    public IMongoCollection<TDocument> GetMongoCollection()
    {
        return _documentCollection;
    }

    public async Task<TDocument?> FindOneAndUpsertAsync(IOctoSession session, FilterDefinition<TDocument> filter,
        UpdateDefinition<TDocument> updateDefinition, ReturnDocument returnDocument = ReturnDocument.After)
    {
        var options = new FindOneAndUpdateOptions<TDocument> { IsUpsert = true, ReturnDocument = returnDocument };
        var document = await _documentCollection.FindOneAndUpdateAsync(((IOctoSessionInternal)session).SessionHandle,
            filter,
            updateDefinition, options);
        return NormalizeRead(document);
    }

    public IAggregateFluent<TDocument> Aggregate(IOctoSession session)
    {
        return _documentCollection.Aggregate(((IOctoSessionInternal)session).SessionHandle,
            new AggregateOptions { AllowDiskUse = true });
    }

    public IAsyncCursor<TOutput> Aggregate<TOutput>(IOctoSession session,
        PipelineDefinition<TDocument, TOutput> pipelineDefinition)
    {
        return _documentCollection.Aggregate(((IOctoSessionInternal)session).SessionHandle, pipelineDefinition);
    }

    public string CollectionName => _documentCollection.CollectionNamespace.CollectionName;

    public async Task CreateAscendingIndexAsync(string name, IEnumerable<string> fields)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        // Fields arrive already fully resolved (e.g. "attributes.timeRange.attributes.from")
        // or as system attributes (e.g. "ckTypeId", "rtState")
        var indexKeys = fields.Select(f => Builders<TDocument>.IndexKeys.Ascending(f));

        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(indexKeys), new CreateIndexOptions { Background = true, Name = name }
        ));
    }

    public async Task CreateTextIndexAsync(string name, string language,
        IEnumerable<CkIndexFields> fields)
    {
        ArgumentValidation.ValidateString(nameof(name), name);
        ArgumentValidation.ValidateString(nameof(language), language);

        var weights = new Dictionary<string, int>();

        var fieldList = fields.ToList();
        // Fields arrive already fully resolved
        var indexKeys =
            fieldList.SelectMany(f => f.AttributeNames).Distinct().Select(f =>
                Builders<TDocument>.IndexKeys.Text(f));

        HashSet<string> attributePaths = new();
        foreach (var field in fieldList.OrderBy(f => f.Weight))
        {
            foreach (var attributePath in field.AttributeNames)
            {
                if (attributePaths.Contains(attributePath.ToLower()))
                {
                    continue; // Skip if already added
                }

                weights.Add(attributePath, field.Weight.GetValueOrDefault(1));
                attributePaths.Add(attributePath.ToLower());
            }
        }

        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(
                indexKeys),
            new CreateIndexOptions { Name = name, Weights = new BsonDocument(weights), DefaultLanguage = language }
        ));
    }

    public async Task CreateUniqueIndexAsync(string name, IEnumerable<string> fields,
        IEnumerable<RtCkId<CkTypeId>> rtCkTypeIds)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        // Fields arrive already fully resolved
        var indexKeys = fields.Select(f => Builders<TDocument>.IndexKeys.Ascending(f));

        // Create partial filter for specific type IDs
        var partialFilterExpression =
            Builders<TDocument>.Filter.In(nameof(RtEntity.CkTypeId).ToCamelCase(), rtCkTypeIds);

        var options = new CreateIndexOptions<TDocument>
        {
            Background = true, Name = name, Unique = true, PartialFilterExpression = partialFilterExpression
        };

        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(indexKeys),
            options
        ));
    }

    public async Task CreateUniqueNotDeletedIndexAsync(string name, IEnumerable<string> fields,
        IEnumerable<RtCkId<CkTypeId>> rtCkTypeIds)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        // Fields arrive already fully resolved
        var indexKeys = fields.Select(f => Builders<TDocument>.IndexKeys.Ascending(f));

        // Create partial filter for active entities (not deleted) and specific type IDs
        var partialFilterExpression = Builders<TDocument>.Filter.And(
            Builders<TDocument>.Filter.In(nameof(RtEntity.RtState).ToCamelCase(), new int?[] { null, 0 }),
            Builders<TDocument>.Filter.In(nameof(RtEntity.CkTypeId).ToCamelCase(), rtCkTypeIds)
        );

        var options = new CreateIndexOptions<TDocument>
        {
            Background = true, Name = name, Unique = true, PartialFilterExpression = partialFilterExpression
        };

        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(indexKeys),
            options
        ));
    }

    public async Task DropIndexAsync(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        var r = await _documentCollection.Indexes.ListAsync();
        foreach (var i in await r.ToListAsync())
        {
            var indexName = i["name"].ToString();
            if (!string.IsNullOrEmpty(indexName) &&
                string.Compare(indexName, name, StringComparison.InvariantCultureIgnoreCase) == 0)
            {
                await _documentCollection.Indexes.DropOneAsync(indexName);
                return;
            }
        }
    }

    public async Task<ICollection<CkTypeIndexWithName>> GetIndexListAsync(string? prefix = null)
    {
        try
        {
            List<CkTypeIndexWithName> indexes = new();

            var r = await _documentCollection.Indexes.ListAsync();
            foreach (var doc in await r.ToListAsync())
            {
                var indexName = doc["name"].ToString();
                if (!string.IsNullOrEmpty(indexName) && indexName != Constants.IdIndexName &&
                    (string.IsNullOrWhiteSpace(prefix) || indexName.StartsWith(prefix)))
                {
                    var fieldsDict = new List<Tuple<string, int>>();
                    var indexType = IndexTypes.Ascending;
                    string? language = null;

                    // Check for unique constraint
                    bool isUnique = false;
                    if (doc.TryGetValue("unique", out var uniqueElement))
                    {
                        isUnique = uniqueElement.AsBoolean;
                    }

                    // Check for partial filter expression (for UniqueNotDeleted)
                    bool hasPartialFilter = false;
                    if (doc.TryGetValue("partialFilterExpression", out var partialFilterElement))
                    {
                        hasPartialFilter = partialFilterElement != null;
                    }

                    if (doc.TryGetValue("key", out var keyElement) && keyElement is BsonDocument keyDoc)
                    {
                        if (keyDoc.TryGetValue("_fts", out var valueElement))
                        {
                            if (valueElement.AsString == "text")
                            {
                                indexType = IndexTypes.Text;

                                if (doc.TryGetValue("default_language", out var languageElement))
                                {
                                    language = languageElement.AsString;
                                }

                                if (doc.TryGetValue("weights", out var keyElement2) &&
                                    keyElement2 is BsonDocument weightsDoc)
                                {
                                    foreach (var elem in weightsDoc.Elements)
                                    {
                                        // Store the full MongoDB field path as-is
                                        fieldsDict.Add(new Tuple<string, int>(elem.Name, elem.Value.AsInt32));
                                    }
                                }
                            }
                            else
                            {
                                throw OperationFailedException.IndexTypeNotSupported(valueElement.AsString);
                            }
                        }
                        else
                        {
                            // Determine if it's a unique index
                            if (isUnique)
                            {
                                indexType = hasPartialFilter ? IndexTypes.UniqueNotDeleted : IndexTypes.Unique;
                            }

                            foreach (var elem in keyDoc.Elements)
                            {
                                // Store the full MongoDB field path as-is (e.g. "attributes.timeRange.attributes.from",
                                // "ckTypeId", "rtState"). Paths are now fully resolved during index creation.
                                fieldsDict.Add(new Tuple<string, int>(elem.Name, elem.Value.AsInt32));
                            }
                        }
                    }

                    List<CkIndexFields> fields = new();
                    if (indexType == IndexTypes.Ascending || indexType == IndexTypes.Unique ||
                        indexType == IndexTypes.UniqueNotDeleted)
                    {
                        fields.Add(new CkIndexFields
                        {
                            Weight = null, AttributeNames = fieldsDict.Select(t => t.Item1).ToList()
                        });
                    }
                    else
                    {
                        foreach (var grouping in fieldsDict.GroupBy(x => x.Item2))
                        {
                            fields.Add(new CkIndexFields
                            {
                                Weight = grouping.Key, AttributeNames = grouping.Select(a => a.Item1).ToList()
                            });
                        }
                    }

                    var index = new CkTypeIndexWithName
                    {
                        Name = indexName, IndexType = indexType, Fields = fields, Language = language
                    };
                    indexes.Add(index);
                }
            }

            return indexes;
        }
        // When MongoCommandException and code 26 (NamespaceNotFound)
        catch (MongoCommandException e) when (e.Code == 26)
        {
            return new List<CkTypeIndexWithName>();
        }
        catch (Exception ex)
        {
            throw OperationFailedException.CannotQueryIndexes(ex);
        }
    }

    public async Task DropAllIndexesAsync(string prefix)
    {
        ArgumentValidation.ValidateString(nameof(prefix), prefix);

        var r = await _documentCollection.Indexes.ListAsync();
        foreach (var i in await r.ToListAsync())
        {
            var indexName = i["name"].ToString();
            if (!string.IsNullOrEmpty(indexName) && indexName.StartsWith(prefix))
            {
                await _documentCollection.Indexes.DropOneAsync(indexName);
            }
        }
    }

    public async Task<ICollection<TDocument>> FindManyAsync(IOctoSession session,
        FilterDefinition<TDocument> filterDefinition,
        SortDefinition<TDocument>? sort = null, int? skip = null, int? take = null)
    {
        try
        {
            var cursor = await _documentCollection.FindAsync(((IOctoSessionInternal)session).SessionHandle,
                filterDefinition, new FindOptions<TDocument> { Sort = sort, Skip = skip, Limit = take });
            return NormalizeRead(await cursor.ToListAsync());
        }
        catch (Exception e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(FindManyAsync), e);
        }
    }

    public async Task<ICollection<TDocument>> FindManyAsync(IOctoSession session,
        Expression<Func<TDocument, bool>> expression,
        int? skip = null,
        int? limit = null)
    {
        try
        {
            var cursor = await _documentCollection.FindAsync(((IOctoSessionInternal)session).SessionHandle,
                expression,
                new FindOptions<TDocument> { Skip = skip, Limit = limit });
            return NormalizeRead(await cursor.ToListAsync());
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(FindManyAsync), e);
        }
    }

    public Task<IQueryable<TDocument>> AsQueryableAsync(IOctoSession? session)
    {
        if (session != null)
        {
            var queryable = GetMongoCollection().AsQueryable(((IOctoSessionInternal)session).SessionHandle);
            return Task.FromResult(queryable);
        }
        else
        {
            var queryable = GetMongoCollection().AsQueryable();
            return Task.FromResult(queryable);
        }
    }

    public IQueryable<TDocument> AsQueryable(IOctoSession? session)
    {
        if (session != null)
        {
            var queryable = GetMongoCollection().AsQueryable(((IOctoSessionInternal)session).SessionHandle);
            return queryable;
        }
        else
        {
            var queryable = GetMongoCollection().AsQueryable();
            return queryable;
        }
    }

    public async Task<TDocument?> FindSingleOrDefaultAsync(IOctoSession session,
        Expression<Func<TDocument, bool>> expression)
    {
        try
        {
            return NormalizeRead(await (await _documentCollection.FindAsync(
                ((IOctoSessionInternal)session).SessionHandle, expression)).SingleOrDefaultAsync());
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(FindSingleOrDefaultAsync), e);
        }
    }

    public async Task<long> GetTotalCountAsync(IOctoSession session)
    {
        return await GetTotalCountAsync(session, document => true);
    }

    public async Task<long> GetTotalCountAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition)
    {
        return await _documentCollection.CountDocumentsAsync(((IOctoSessionInternal)session).SessionHandle,
            filterDefinition);
    }

    public async Task<long> GetTotalCountAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition,
        long limit)
    {
        return await _documentCollection.CountDocumentsAsync(((IOctoSessionInternal)session).SessionHandle,
            filterDefinition, new CountOptions { Limit = limit });
    }


    public async Task<long> GetTotalCountAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression)
    {
        return await _documentCollection.CountDocumentsAsync(((IOctoSessionInternal)session).SessionHandle,
            expression);
    }

    public async Task UpdateOneAsync(IOctoSession session, IEnumerable<TDocument> documents)
    {
        foreach (var document in documents)
        {
            var id = _mongoDataSourceMapper.GetId(document);
            var filterDefinition = Builders<TDocument>.Filter.BuildIdFilter(id);
            var updateDefinition = _mongoDataSourceMapper.ApplyUpdate(document);

            if (RequiresNullAttributesRepair(document))
            {
                // AB#5148 defense in depth: a document persisted before the serializer fix may
                // carry an explicit `attributes: null`, under which MongoDB refuses to create a
                // subpath (write error code 28). One ordered bulk write first normalizes the
                // stored null to an empty document and then applies the subpath $set update, so
                // the first write heals the entity instead of erroring — with no extra round
                // trip and inside the ambient transaction.
                var bulkResult = await _documentCollection.BulkWriteAsync(
                    ((IOctoSessionInternal)session).SessionHandle,
                    new WriteModel<TDocument>[]
                    {
                        CreateNullAttributesRepairModel(id),
                        new UpdateOneModel<TDocument>(filterDefinition, updateDefinition)
                    },
                    new BulkWriteOptions { IsOrdered = true });
                ThrowIfNotAcknowledged(bulkResult.IsAcknowledged);
                // The repair filter is the id filter narrowed by `attributes: null`, so it can
                // only match when the main update matches too — an aggregate MatchedCount of 0
                // still means "document not found".
                ThrowIfMatchedCountZero<TDocument>(bulkResult.MatchedCount, id);
            }
            else
            {
                var result = await _documentCollection.UpdateOneAsync(((IOctoSessionInternal)session).SessionHandle,
                    filterDefinition,
                    updateDefinition);
                ThrowIfNotAcknowledged(result.IsAcknowledged);
                ThrowIfMatchedCountZero<TDocument>(result.MatchedCount, id);
            }
        }
    }

    /// <summary>
    ///     AB#5148: only <see cref="RtEntityMongoDataSourceMapper{TEntity}" /> produces
    ///     `attributes.&lt;name&gt;` subpath $sets, and only when the partial document carries
    ///     attribute values — everything else cannot hit the attributes-null write conflict.
    /// </summary>
    private static bool RequiresNullAttributesRepair(TDocument document)
    {
        return document is RtEntity { Attributes.Count: > 0 };
    }

    /// <summary>
    ///     Builds the repair statement that normalizes a stored `attributes: null` to an empty
    ///     document for the given id. Raw BSON definitions on purpose: the typed field renderer
    ///     would resolve the `attributes` member through
    ///     <see cref="Serialization.RtAttributeDictionarySerializer" />, which never produces a
    ///     BSON null/empty-document pair for a filter value.
    /// </summary>
    private static UpdateOneModel<TDocument> CreateNullAttributesRepairModel(TKey id)
    {
        var repairFilter = Builders<TDocument>.Filter.And(
            Builders<TDocument>.Filter.BuildIdFilter(id),
            new BsonDocument(Constants.AttributesName, BsonNull.Value));
        var repairUpdate = new BsonDocumentUpdateDefinition<TDocument>(
            new BsonDocument("$set", new BsonDocument(Constants.AttributesName, new BsonDocument())));
        return new UpdateOneModel<TDocument>(repairFilter, repairUpdate);
    }

    public async Task<bool> UpdateOneIfGuardMatchesAsync(IOctoSession session, TDocument document,
        AttributeNewerThanGuard guard)
    {
        // Optimistic-concurrency filter: apply only if the persisted value at
        // guard.AttributePath is missing, null, or <= guard.NewValue. A strictly-newer
        // persisted value means a later writer has already won — we skip silently. <= (not
        // <) makes ties idempotent: two writes with identical timestamps both apply, last-
        // arriving wins on the actual document content. The race this protects against has
        // monotonically increasing timestamps in practice (each writer captures
        // DateTime.UtcNow before its DB roundtrip), so ties are extremely rare regardless.
        var id = _mongoDataSourceMapper.GetId(document);

        if (RequiresNullAttributesRepair(document))
        {
            // AB#5148 defense in depth (see UpdateOneAsync): normalize a stored
            // `attributes: null` before the guarded update. A separate statement rather than an
            // ordered bulk write, because the guarded update's own MatchedCount is this method's
            // return value — folding both into one bulk write would report a heal-only write
            // (guard rejected, repair matched) as an applied update.
            var repairModel = CreateNullAttributesRepairModel(id);
            await _documentCollection.UpdateOneAsync(((IOctoSessionInternal)session).SessionHandle,
                repairModel.Filter, repairModel.Update);
        }

        var idFilter = Builders<TDocument>.Filter.BuildIdFilter(id);
        var bsonNewValue = BsonValue.Create(guard.NewValue);
        var guardFilter = Builders<TDocument>.Filter.Or(
            Builders<TDocument>.Filter.Not(Builders<TDocument>.Filter.Exists(guard.AttributePath)),
            Builders<TDocument>.Filter.Eq(guard.AttributePath, BsonNull.Value),
            Builders<TDocument>.Filter.Lte(guard.AttributePath, bsonNewValue));
        var combinedFilter = Builders<TDocument>.Filter.And(idFilter, guardFilter);

        var updateDefinition = _mongoDataSourceMapper.ApplyUpdate(document);
        var result = await _documentCollection.UpdateOneAsync(((IOctoSessionInternal)session).SessionHandle,
            combinedFilter, updateDefinition);
        ThrowIfNotAcknowledged(result.IsAcknowledged);
        // MatchedCount=0 is the intended outcome when the guard rejects a stale write —
        // do NOT throw. The caller can detect a no-op via the return value.
        return result.MatchedCount > 0;
    }

    public async Task<bool> UpdateOneWithRecordArrayItemsAsync(IOctoSession session, TDocument document,
        IReadOnlyCollection<RtRecordArrayItemUpdate> itemUpdates, bool requireItemMatch)
    {
        // AB#7445: ONE atomic statement - the regular attribute updates (ApplyUpdate: attributes, rtChangedDateTime,
        // $inc rtVersion) plus $set on attributes.<array>.$[iN].attributes.<member> with arrayFilters on the match
        // member. The stored document is neither read nor version-checked, so a concurrent update of another item (or
        // the same item) of the same entity cannot be lost; the same item keeps the last write.
        var id = _mongoDataSourceMapper.GetId(document);
        var statement = RecordArrayItemUpdateStatement<TDocument>.Create(itemUpdates);
        var sessionHandle = ((IOctoSessionInternal)session).SessionHandle;
        var hasRegularAttributes = RequiresNullAttributesRepair(document);

        if (hasRegularAttributes)
        {
            // AB#5148: a stored `attributes: null` would make the subpath $sets fail (see UpdateOneAsync).
            var repairModel = CreateNullAttributesRepairModel(id);
            await _documentCollection.UpdateOneAsync(sessionHandle, repairModel.Filter, repairModel.Update);
        }

        var filter = Builders<TDocument>.Filter.And(Builders<TDocument>.Filter.BuildIdFilter(id),
            statement.ArraysExistFilter);
        if (requireItemMatch)
        {
            // Nothing else to write: no matching item means no write at all (no rtVersion bump).
            filter = Builders<TDocument>.Filter.And(filter, statement.AnyItemMatchesFilter);
        }

        var update = Builders<TDocument>.Update.Combine(
            new[] { _mongoDataSourceMapper.ApplyUpdate(document) }.Concat(statement.Sets));
        var result = await _documentCollection.UpdateOneAsync(sessionHandle, filter, update,
            new UpdateOptions { ArrayFilters = statement.ArrayFilters });
        ThrowIfNotAcknowledged(result.IsAcknowledged);
        if (result.MatchedCount > 0)
        {
            return true;
        }

        if (requireItemMatch)
        {
            return false;
        }

        // Regular attributes to write but an array is missing (or null) on the stored entity, so no item can match:
        // write the regular attributes alone; a missing entity is an error as for any update.
        var fallback = await _documentCollection.UpdateOneAsync(sessionHandle,
            Builders<TDocument>.Filter.BuildIdFilter(id), _mongoDataSourceMapper.ApplyUpdate(document));
        ThrowIfNotAcknowledged(fallback.IsAcknowledged);
        ThrowIfMatchedCountZero<TDocument>(fallback.MatchedCount, id);
        return true;
    }

    public async Task UpdateManyAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression,
        TDocument document)
    {
        var updateDefinition = _mongoDataSourceMapper.ApplyUpdate(document);
        var result = await _documentCollection.UpdateManyAsync(((IOctoSessionInternal)session).SessionHandle,
            expression,
            updateDefinition);
        ThrowIfNotAcknowledged(result.IsAcknowledged);
    }

    public async Task ReplaceManyAsync(IOctoSession session, IEnumerable<TDocument> documents)
    {
        foreach (var document in documents)
        {
            var id = _mongoDataSourceMapper.GetId(document);
            var filterDefinition = Builders<TDocument>.Filter.BuildIdFilter(id);
            var result = await _documentCollection.ReplaceOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filterDefinition,
                document);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero<TDocument>(result.MatchedCount, id);
        }
    }

    public async Task ReplaceByIdAsync(IOctoSession session, TKey id, TDocument document)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var result = await _documentCollection.ReplaceOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filter, document);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero<TDocument>(result.MatchedCount, id);
        }
        catch (MongoWriteException ex)
        {
            _logger.LogError(ex, "Error replacing document with id {Id}", id);
            throw CreateWriteException<TDocument>(nameof(ReplaceByIdAsync), ex);
        }
    }

    public async Task ReplaceOneAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition,
        TDocument entity)
    {
        try
        {
            var result = await _documentCollection.ReplaceOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filterDefinition, entity);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero(result.MatchedCount, filterDefinition);
        }
        catch (MongoWriteException ex)
        {
            _logger.LogError(ex, "Error replacing document with filter {@FilterDefinition}", filterDefinition);
            throw CreateWriteException<TDocument>(nameof(ReplaceOneAsync), ex);
        }
    }

    public async Task<long> ReplaceOneWithModificationCountAsync(IOctoSession session,
        FilterDefinition<TDocument> filterDefinition, TDocument entity)
    {
        try
        {
            var result = await _documentCollection.ReplaceOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filterDefinition, entity);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            return result.ModifiedCount;
        }
        catch (MongoWriteException ex)
        {
            _logger.LogError(ex, "Error replacing document with filter {@FilterDefinition}", filterDefinition);
            throw CreateWriteException<TDocument>(nameof(ReplaceOneWithModificationCountAsync), ex);
        }
    }

    public async Task UpdateOneAsync(IOctoSession session, TKey id,
        UpdateDefinition<TDocument> updateDefinition)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var result = await _documentCollection.UpdateOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filter, updateDefinition);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero<TDocument>(result.MatchedCount, id);
        }
        catch (MongoWriteException ex)
        {
            _logger.LogError(ex, "Error updating document with id {Id}", id);
            throw CreateWriteException<TDocument>(nameof(UpdateOneAsync), ex);
        }
    }

    public async Task UpdateManyAsync(IOctoSession session, FilterDefinition<TDocument> filter,
        UpdateDefinition<TDocument> updateDefinition)
    {
        try
        {
            var result = await _documentCollection.UpdateManyAsync(((IOctoSessionInternal)session).SessionHandle,
                filter, updateDefinition);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
        }
        catch (MongoWriteException ex)
        {
            _logger.LogError(ex, "Error updating documents with filter {@FilterDefinition}", filter);
            throw CreateWriteException<TDocument>(nameof(UpdateManyAsync), ex);
        }
    }

    public async Task<TDocument?> DocumentAsync(IOctoSession session, TKey key)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(key);
            var result = await _documentCollection.FindAsync(
                ((IOctoSessionInternal)session).SessionHandle, filter);

            var document = await result.SingleOrDefaultAsync();
            if (document == null)
            {
                return null;
            }

            return NormalizeRead(document);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DocumentAsync), e);
        }
    }

    public async Task<IReadOnlyList<TDocument>> DocumentsAsync(IOctoSession session, IEnumerable<TKey> keys)
    {
        try
        {
            List<FilterDefinition<TDocument>> filters = new();
            foreach (var key in keys)
            {
                var filter = Builders<TDocument>.Filter.BuildIdFilter(key);
                filters.Add(filter);
            }

            var orFilter = Builders<TDocument>.Filter.Or(filters);
            var result = await _documentCollection.FindAsync(
                ((IOctoSessionInternal)session).SessionHandle, orFilter);

            var documents = await result.ToListAsync();

            return NormalizeRead(documents);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DocumentAsync), e);
        }
    }

    async Task<TDerived?> IDataSourceCollection<TKey, TDocument>.DocumentAsync<TDerived>(IOctoSession session, TKey key)
        where TDerived : default
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(key);
            var result = await _documentCollection.FindAsync(
                ((IOctoSessionInternal)session).SessionHandle, filter);

            var document = await result.SingleOrDefaultAsync();
            if (document == null)
            {
                return default;
            }

            return (TDerived)NormalizeRead(document)!;
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DocumentAsync), e);
        }
    }

    public async Task<IEnumerable<TDocument>> GetAsync(IOctoSession session, int? skip = null, int? take = null)
    {
        try
        {
            var options = new FindOptions<TDocument> { Limit = take, Skip = skip };
            return NormalizeRead(await (await _documentCollection.FindAsync(
                ((IOctoSessionInternal)session).SessionHandle, _ => true, options)).ToListAsync());
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(GetAsync), e);
        }
    }

    public async Task<bool> TryDeleteOneAsync(IOctoSession session, TKey id)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            return deleteResult.DeletedCount > 0;
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(TryDeleteOneAsync), e);
        }
    }

    public async Task<bool> TryDeleteOneAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, expression);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);

            return deleteResult.DeletedCount > 0;
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteOneAsync), e);
        }
    }

    public async Task DeleteOneAsync(IOctoSession session, TKey id)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero(deleteResult.DeletedCount, id);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteOneAsync), e);
        }
    }

    public async Task DeleteOneAsync(IOctoSession session, FilterDefinition<TDocument> filter)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero(deleteResult.DeletedCount, filter);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteOneAsync), e);
        }
    }

    public async Task DeleteOneAsync(IOctoSession session, IEnumerable<TKey> ids)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.In(Constants.IdField, ids);

            var deleteResult =
                await _documentCollection.DeleteManyAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero(deleteResult.DeletedCount);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteManyAsync), e);
        }
    }

    public async Task DeleteManyAsync(IOctoSession session, FilterDefinition<TDocument> filter)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteManyAsync(((IOctoSessionInternal)session).SessionHandle,
                    filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteManyAsync), e);
        }
    }

    public async Task<IBulkImportResult> BulkImportAsync(IOctoSession session, IEnumerable<TDocument> documents,
        BulkOperationOptions options)
    {
        var listWrites = new List<WriteModel<TDocument>>();
        try
        {
            List<RtAssociation> existingAssociations = new();

            if (options.InsertStrategy == BulkInsertStrategies.Upsert)
            {
                // For associations, we do not have an id, so we need to check if the association already exists
                // because if the filter doesn't contain the id field, the upsert will not call the IdGenerator
                // and we end up with associations with id 0 and this association is used for every import.
                // So we need to find all existing associations first
                // and then only insert the new ones
                var filterConditions = documents.OfType<RtAssociation>().Select(association =>
                    Builders<TDocument>.Filter.And(
                        Builders<TDocument>.Filter.BuildAssociationFilter(association))).ToList();

                if (filterConditions.Any())
                {
                    var combinedFilter = Builders<TDocument>.Filter.Or(filterConditions);
                    existingAssociations = (await _documentCollection
                            .Find(((IOctoSessionInternal)session).SessionHandle, combinedFilter).ToListAsync())
                        .OfType<RtAssociation>().ToList();
                }
            }

            foreach (var v in documents)
            {
                switch (options.InsertStrategy)
                {
                    case BulkInsertStrategies.InsertOnly:
                        if (v is RtEntity)
                        {
                            // AB#7018: an ARCHIVED document with the same rtId (the default GraphQL delete only
                            // archives) is free: it is replaced by the incoming entity, which is active again.
                            // An ACTIVE document with that rtId still fails with a duplicate key on the primary key,
                            // because the replace filter does not match it and the upsert then tries to insert it.
                            // One atomic statement per entity, so no read-then-write race.
                            listWrites.Add(new ReplaceOneModel<TDocument>(
                                Builders<TDocument>.Filter.And(
                                    Builders<TDocument>.Filter.BuildIdFilter(_mongoDataSourceMapper.GetId(v)),
                                    ArchivedStateFilter()),
                                v) { IsUpsert = true });
                        }
                        else
                        {
                            listWrites.Add(new InsertOneModel<TDocument>(v));
                        }

                        break;
                    case BulkInsertStrategies.Upsert:
                        if (v is RtAssociation association)
                        {
                            var existing = existingAssociations.FirstOrDefault(a =>
                                a.OriginRtId == association.OriginRtId &&
                                a.TargetRtId == association.TargetRtId &&
                                a.AssociationRoleId == association.AssociationRoleId);

                            // Only insert if not existing otherwise just skip because the association already exists
                            if (existing == null)
                            {
                                listWrites.Add(new InsertOneModel<TDocument>(v));
                            }
                        }
                        else
                        {
                            listWrites.Add(new ReplaceOneModel<TDocument>(
                                Builders<TDocument>.Filter.BuildIdFilter(_mongoDataSourceMapper.GetId(v)),
                                v) { IsUpsert = true });
                        }

                        break;
                }
            }

            BulkWriteResult<TDocument>? result = null;
            if (listWrites.Any())
            {
                result =
                    await _documentCollection.BulkWriteAsync(((IOctoSessionInternal)session).SessionHandle, listWrites);
            }

            return new BulkImportResult(result);
        }
        catch (MongoBulkWriteException<TDocument> e)
        {
            if (e.WriteErrors.Any(x => x.Category == ServerErrorCategory.DuplicateKey))
            {
                throw await DescribeBulkDuplicateKeyAsync(e, listWrites).ConfigureAwait(false);
            }

            throw OperationFailedException.BulkImportError(e);
        }
        catch (MongoBulkWriteException e)
        {
            throw OperationFailedException.BulkImportError(e);
        }
    }

    private const string RtStateField = "rtState";

    /// <summary>
    ///     AB#7018: turns the raw E11000 of a bulk write into a message an operator can act on: the entity that
    ///     was written, the violated index, and the state (Active / Archived) of the document that blocks it.
    ///     The lookup runs without the transaction session: the failed write has aborted the transaction, and the
    ///     blocking document was committed before.
    /// </summary>
    private async Task<Exception> DescribeBulkDuplicateKeyAsync(MongoBulkWriteException<TDocument> exception,
        IReadOnlyList<WriteModel<TDocument>> writes)
    {
        var error = exception.WriteErrors.First(x => x.Category == ServerErrorCategory.DuplicateKey);
        try
        {
            var written = error.Index >= 0 && error.Index < writes.Count ? GetWrittenDocument(writes[error.Index]) : default;
            var parsed = DuplicateKeyMessageParser.Parse(error.Message);
            var entity = written is RtEntity rtEntity
                ? $"entity of type '{rtEntity.CkTypeId}' with rtId '{rtEntity.RtId}'"
                : $"document of type '{typeof(TDocument).Name}'";

            string description;
            if (parsed.IndexName == IdIndexName && written != null)
            {
                var state = await FindStateAsync(Builders<TDocument>.Filter.BuildIdFilter(_mongoDataSourceMapper.GetId(written)))
                    .ConfigureAwait(false);
                description = $"Cannot insert {entity}: a document with this id already exists in state " +
                              $"{state ?? "unknown"} (primary key '{IdIndexName}' of collection '{CollectionName}'). " +
                              "Import with replace (Upsert) to overwrite it.";
            }
            else
            {
                string? state = null;
                string? blockerId = null;
                var keyFilter = DuplicateKeyMessageParser.BuildKeyFilter<TDocument>(parsed.DupKey);
                if (keyFilter != null)
                {
                    var blocker = await FindBlockerAsync(keyFilter).ConfigureAwait(false);
                    state = blocker.State;
                    blockerId = blocker.Id;
                }

                description = $"Cannot write {entity}: the value {parsed.DupKey ?? "(unknown)"} violates the unique index " +
                              $"'{parsed.IndexName ?? "(unknown)"}' of collection '{CollectionName}'. The blocking document" +
                              $"{(blockerId != null ? $" '{blockerId}'" : "")} is in state {state ?? "unknown"}.";
                if (state == nameof(RtState.Archived))
                {
                    description += $" Index '{parsed.IndexName}' is a plain Unique index: it also blocks values of " +
                                   "archived (deleted) entities. Erase the archived entity (delete with strategy Erase) " +
                                   "or let the CK type declare the index as UniqueNotDeleted.";
                }
            }

            return OperationFailedException.BulkImportDuplicateKey(description, exception);
        }
        catch (Exception lookupFailure) when (lookupFailure is not OperationCanceledException)
        {
            // The diagnosis is a convenience: never hide the original failure behind a failed lookup.
            _logger.LogWarning(lookupFailure, "Could not determine the state of the document blocking a duplicate key");
            return OperationFailedException.BulkImportError(exception);
        }
    }

    private const string IdIndexName = "_id_";

    private static TDocument? GetWrittenDocument(WriteModel<TDocument> write)
    {
        return write switch
        {
            InsertOneModel<TDocument> insert => insert.Document,
            ReplaceOneModel<TDocument> replace => replace.Replacement,
            _ => default
        };
    }

    private async Task<string?> FindStateAsync(FilterDefinition<TDocument> filter)
    {
        var found = await _documentCollection.Find(filter)
            .Project<BsonDocument>(Builders<TDocument>.Projection.Include(RtStateField))
            .FirstOrDefaultAsync().ConfigureAwait(false);
        return found == null ? null : DescribeState(found);
    }

    private async Task<(string? Id, string? State)> FindBlockerAsync(FilterDefinition<TDocument> filter)
    {
        var found = await _documentCollection.Find(filter)
            .Project<BsonDocument>(Builders<TDocument>.Projection.Include(RtStateField))
            .FirstOrDefaultAsync().ConfigureAwait(false);
        return found == null
            ? (null, null)
            : (found.TryGetValue("_id", out var id) ? id.ToString() : null, DescribeState(found));
    }

    private static string DescribeState(BsonDocument document)
    {
        // A missing rtState is an active document (the partial filter of UniqueNotDeleted reads null/0 as active).
        var state = document.TryGetValue(RtStateField, out var value) && value.IsInt32 ? value.AsInt32 : 0;
        return state == (int)RtState.Archived ? nameof(RtState.Archived) : nameof(RtState.Active);
    }

    private static FilterDefinition<TDocument> ArchivedStateFilter()
    {
        return Builders<TDocument>.Filter.Eq(RtStateField, (int)RtState.Archived);
    }


    public IUpdateStream<TDocument> WatchAsync(UpdateTypes updateTypes,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentFilterFunc = null,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentBeforeFilterFunc = null,
        CancellationToken cancellationToken = default)
    {
        // AB#5533: change-stream documents (full document and pre-image) are normalised like reads.
        var updateStream = new UpdateStream<TDocument>(_readNormalizer);

        PipelineDefinition<ChangeStreamDocument<TDocument>, ChangeStreamDocument<TDocument>> pipeline =
            new EmptyPipelineDefinition<ChangeStreamDocument<TDocument>>();

        pipeline = pipeline.Match(x =>
            (x.OperationType == ChangeStreamOperationType.Update &&
             updateTypes.HasFlag(UpdateTypes.Update)) ||
            (x.OperationType == ChangeStreamOperationType.Insert &&
             updateTypes.HasFlag(UpdateTypes.Insert)) ||
            (x.OperationType == ChangeStreamOperationType.Delete &&
             updateTypes.HasFlag(UpdateTypes.Delete)) ||
            (x.OperationType == ChangeStreamOperationType.Replace &&
             updateTypes.HasFlag(UpdateTypes.Replace)) ||
            updateTypes == UpdateTypes.Undefined
        );

        FilterDefinition<ChangeStreamDocument<TDocument>>? documentFilter = null;
        FilterDefinition<ChangeStreamDocument<TDocument>>? documentBeforeFilter = null;

        if (documentFilterFunc != null)
        {
            documentFilter = documentFilterFunc();
        }

        if (documentBeforeFilterFunc != null)
        {
            documentBeforeFilter = documentBeforeFilterFunc();
        }

        var combinedFilter = BuildExtensions.ComposeWatchFilter(documentFilter, documentBeforeFilter);
        if (combinedFilter != null)
        {
            pipeline = pipeline.Match(combinedFilter);
        }

        updateStream.Watch(_documentCollection, pipeline, cancellationToken);

        return updateStream;
    }

    public async Task DeleteOneAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, expression);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero(deleteResult.DeletedCount, expression);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteOneAsync), e);
        }
    }

    public async Task DeleteManyAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteManyAsync(((IOctoSessionInternal)session).SessionHandle,
                    expression);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(DeleteManyAsync), e);
        }
    }

    #region Insert operations

    public async Task InsertManyAsync(IOctoSession session, IEnumerable<TDocument> documentCollection)
    {
        try
        {
            await _documentCollection.InsertManyAsync(((IOctoSessionInternal)session).SessionHandle,
                documentCollection);
        }
        catch (MongoWriteException ex)
        {
            throw CreateWriteException<TDocument>(nameof(InsertManyAsync), ex);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(InsertManyAsync), e);
        }
    }

    public async Task InsertOneAsync(IOctoSession session, TDocument document)
    {
        try
        {
            await _documentCollection.InsertOneAsync(((IOctoSessionInternal)session).SessionHandle, document);
        }
        catch (MongoWriteException ex)
        {
            throw CreateWriteException<TDocument>(nameof(InsertOneAsync), ex);
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(InsertOneAsync), e);
        }
    }

    #endregion Insert operations

    #region Exception helpers

    private Exception CreateWriteException<T>(string operationName, MongoWriteException ex)
    {
        if (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return DuplicateKeyException.DuplicateKeyError(typeof(T), ex);
        }

        return OperationFailedException.DatabaseOperationFailed(operationName, ex);
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void ThrowIfNotAcknowledged(bool acknowledged)
    {
        if (!acknowledged)
        {
            throw new MongoException("The action was not acknowledged.");
        }
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void ThrowIfMatchedCountZero<T>(long matchedCount, TKey id)
    {
        if (matchedCount == 0)
        {
            throw EntityNotFoundException.IdNotFound(typeof(T).Name, id.ToString() ?? "unknown");
        }
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void ThrowIfMatchedCountZero(long matchedCount, Expression<Func<TDocument, bool>> expression)
    {
        if (matchedCount == 0)
        {
            throw EntityNotFoundException.FilterNotMatching<TDocument>(expression.ToString());
        }
    }

    private void ThrowIfMatchedCountZero(long matchedCount, FilterDefinition<TDocument> filter)
    {
        if (matchedCount == 0)
        {
            throw EntityNotFoundException.FilterNotMatching<TDocument>(filter.ToString() ?? "");
        }
    }

    private void ThrowIfMatchedCountZero<TField>(long matchedCount, TField idField)
    {
        if (matchedCount == 0)
        {
            throw EntityNotFoundException.FilterNotMatching<TDocument>(idField?.ToString() ?? "");
        }
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void ThrowIfMatchedCountZero(long matchedCount)
    {
        if (matchedCount == 0)
        {
            throw EntityNotFoundException.NoDataMatched();
        }
    }

    #endregion Exception helpers
}
