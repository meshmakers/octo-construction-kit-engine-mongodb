using System.Linq.Expressions;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
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

    internal MongoDbDataSourceCollection(ILogger<MongoDbDataSourceCollection<TKey, TDocument>> logger, IMongoCollection<TDocument> documentCollection,
        IMongoDataSourceMapper<TKey, TDocument> mongoDataSourceMapper)
    {
        _logger = logger;
        _mongoDataSourceMapper = mongoDataSourceMapper;
        _documentCollection = documentCollection;
    }

    public IMongoDataSourceMapper<TKey, TDocument> MongoDataSourceMapper => _mongoDataSourceMapper;

    public IMongoCollection<TDocument> GetMongoCollection()
    {
        return _documentCollection;
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

        var indexKeys =
            fields.Select(f =>
                Constants.IsSystemAttribute(f)
                    ? Builders<TDocument>.IndexKeys.Ascending(f)
                    : Builders<TDocument>.IndexKeys.Ascending(Constants.AttributesName + "." + f.ToCamelCase()));


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
        var indexKeys =
            fieldList.SelectMany(f => f.AttributeNames).Distinct().Select(f =>
                Builders<TDocument>.IndexKeys.Text(Constants.AttributesName + "." + f.ToCamelCase()));

        HashSet<string> attributePaths = new();
        foreach (var field in fieldList.OrderBy(f => f.Weight))
        {
            foreach (var attributePath in field.AttributeNames)
            {
                if (attributePaths.Contains(attributePath.ToLower()))
                {
                    continue; // Skip if already added
                }

                weights.Add(Constants.AttributesName + "." + attributePath.ToCamelCase(),
                    field.Weight.GetValueOrDefault(1));
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
        IEnumerable<CkId<CkTypeId>> typeIds)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        var indexKeys =
            fields.Select(f =>
                Constants.IsSystemAttribute(f)
                    ? Builders<TDocument>.IndexKeys.Ascending(f)
                    : Builders<TDocument>.IndexKeys.Ascending(Constants.AttributesName + "." + f.ToCamelCase()));

        // Create partial filter for specific type IDs
        var partialFilterExpression = Builders<TDocument>.Filter.In(nameof(RtEntity.CkTypeId).ToCamelCase(), typeIds);

        var options = new CreateIndexOptions<TDocument>
        {
            Background = true,
            Name = name,
            Unique = true,
            PartialFilterExpression = partialFilterExpression
        };

        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(indexKeys),
            options
        ));
    }

    public async Task CreateUniqueNotDeletedIndexAsync(string name, IEnumerable<string> fields,
        IEnumerable<CkId<CkTypeId>> typeIds)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        var indexKeys =
            fields.Select(f =>
                Constants.IsSystemAttribute(f)
                    ? Builders<TDocument>.IndexKeys.Ascending(f)
                    : Builders<TDocument>.IndexKeys.Ascending(Constants.AttributesName + "." + f.ToCamelCase()));

        // Create partial filter for active entities (not deleted) and specific type IDs
        var partialFilterExpression = Builders<TDocument>.Filter.And(
            Builders<TDocument>.Filter.In(nameof(RtEntity.RtState).ToCamelCase(), new int?[] { null, 0 }),
            Builders<TDocument>.Filter.In(nameof(RtEntity.CkTypeId).ToCamelCase(), typeIds)
        );

        var options = new CreateIndexOptions<TDocument>
        {
            Background = true,
            Name = name,
            Unique = true,
            PartialFilterExpression = partialFilterExpression
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
                                    var attributePath = elem.Name;
                                    if (attributePath.StartsWith(Constants.AttributesName + "."))
                                    {
                                        attributePath = attributePath.Substring(Constants.AttributesName.Length + 1);
                                    }

                                    fieldsDict.Add(new Tuple<string, int>(attributePath, elem.Value.AsInt32));
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
                            var attributePath = elem.Name;

                            if (attributePath.StartsWith(Constants.AttributesName + "."))
                            {
                                var tmp = attributePath.Substring(Constants.AttributesName.Length + 1);
                                // Fix to ensure that we can wrongly create indexes with "attributes._id"
                                // or "attributes.rtWellKnownName"
                                if (!Constants.IsSystemAttribute(tmp))
                                {
                                    attributePath = tmp;
                                }
                            }

                            fieldsDict.Add(new Tuple<string, int>(attributePath, elem.Value.AsInt32));
                        }
                    }
                }

                List<CkIndexFields> fields = new();
                if (indexType == IndexTypes.Ascending || indexType == IndexTypes.Unique || indexType == IndexTypes.UniqueNotDeleted)
                {
                    fields.Add(new CkIndexFields { Weight = null, AttributeNames = fieldsDict.Select(t=> t.Item1).ToList() });
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
            return await cursor.ToListAsync();
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
            return await cursor.ToListAsync();
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
            return await (await _documentCollection.FindAsync(((IOctoSessionInternal)session).SessionHandle,
                expression)).SingleOrDefaultAsync();
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


    public async Task<long> GetTotalCountAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression)
    {
        return await _documentCollection.CountDocumentsAsync(((IOctoSessionInternal)session).SessionHandle,
            expression);
    }

    public async Task UpdateManyAsync(IOctoSession session, IEnumerable<TDocument> documents)
    {
        foreach (var document in documents)
        {
            var id = _mongoDataSourceMapper.GetId(document);
            var filterDefinition = Builders<TDocument>.Filter.BuildIdFilter(id);
            var updateDefinition = _mongoDataSourceMapper.ApplyUpdate(document);
            var result = await _documentCollection.UpdateOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filterDefinition,
                updateDefinition);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero<TDocument>(result.MatchedCount, id);
        }
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
            HandleWriteException<TDocument>(ex);
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
            HandleWriteException<TDocument>(ex);
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
            HandleWriteException<TDocument>(ex);
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

            return document;
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

            return documents;
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

            return (TDerived)document;
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
            return await (await _documentCollection.FindAsync(((IOctoSessionInternal)session).SessionHandle,
                _ => true, options)).ToListAsync();
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

    public async Task DeleteManyAsync(IOctoSession session, IEnumerable<TKey> ids)
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
        try
        {
            var listWrites = new List<WriteModel<TDocument>>();
            foreach (var v in documents)
            {
                switch (options.InsertStrategy)
                {
                    case BulkInsertStrategy.InsertOnly:
                        listWrites.Add(new InsertOneModel<TDocument>(v));
                        break;
                    case BulkInsertStrategy.Upsert:
                        listWrites.Add(new ReplaceOneModel<TDocument>(
                            Builders<TDocument>.Filter.BuildIdFilter(_mongoDataSourceMapper.GetId(v)),
                            v) { IsUpsert = true });
                        break;
                }
            }

            var result =
                await _documentCollection.BulkWriteAsync(((IOctoSessionInternal)session).SessionHandle, listWrites);
            return new BulkImportResult(result);
        }
        catch (MongoBulkWriteException e)
        {
            throw OperationFailedException.BulkImportError(e);
        }
    }


    public IUpdateStream<TDocument> WatchAsync(UpdateTypes updateTypes,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentFilterFunc = null,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentBeforeFilterFunc = null,
        CancellationToken cancellationToken = default)
    {
        var updateStream = new UpdateStream<TDocument>();

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

        if (documentFilter != null && documentBeforeFilter != null)
        {
            pipeline = pipeline.Match(Builders<ChangeStreamDocument<TDocument>>
                .Filter.Or(documentFilter, documentBeforeFilter));
        }
        else if (documentFilter != null)
        {
            pipeline = pipeline.Match(documentFilter);
        }
        else if (documentBeforeFilter != null)
        {
            pipeline = pipeline.Match(documentBeforeFilter);
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
            ThrowIfMatchedCountZero(deleteResult.DeletedCount, expression);
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
            HandleWriteException<TDocument>(ex);
            throw;
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
            HandleWriteException<TDocument>(ex);
            throw;
        }
        catch (MongoException e)
        {
            throw OperationFailedException.DatabaseOperationFailed(nameof(InsertOneAsync), e);
        }
    }

    #endregion Insert operations

    #region Exception helpers

    private void HandleWriteException<T>(MongoWriteException ex)
    {
        if (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw DuplicateKeyException.DuplicateKeyError(typeof(T), ex);
        }

        throw OperationFailedException.DatabaseOperationFailed(nameof(HandleWriteException), ex);
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
            var message = $"Operation failed because ID '{id}' is not existing for document type {nameof(T)}.";
            throw EntityNotFoundException.IdNotFound(nameof(T), message);
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
