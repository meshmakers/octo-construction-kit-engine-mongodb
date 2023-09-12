using System.Linq.Expressions;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;
using NLog;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

internal class DatabaseCollection<TDocument> : IDatabaseCollection<TDocument>
    where TDocument : class, new()

{
    // ReSharper disable once StaticMemberInGenericType
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IMongoCollection<TDocument> _documentCollection;

    internal DatabaseCollection(IMongoCollection<TDocument> documentCollection)
    {
        _documentCollection = documentCollection;
    }

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

    public async Task CreateAscendingIndexAsync(string name, IEnumerable<string> fields)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        var indexKeys =
            fields.Select(f =>
                Builders<TDocument>.IndexKeys.Ascending(Constants.AttributesName + "." + f.ToCamelCase()));


        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(indexKeys), new CreateIndexOptions
            {
                Name = name
            }
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
            fieldList.SelectMany(f => f.AttributeNames).Select(f =>
                Builders<TDocument>.IndexKeys.Text(Constants.AttributesName + "." + f.ToCamelCase()));

        foreach (var field in fieldList)
        foreach (var attributeName in field.AttributeNames)
        {
            weights.Add(Constants.AttributesName + "." + attributeName.ToCamelCase(),
                field.Weight.GetValueOrDefault(1));
        }

        await _documentCollection.Indexes.CreateOneAsync(new CreateIndexModel<TDocument>(
            Builders<TDocument>.IndexKeys.Combine(
                indexKeys), new CreateIndexOptions
            {
                Name = name,
                Weights = new BsonDocument(weights),
                DefaultLanguage = language
            }
        ));
    }

    public async Task DropIndexAsync(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        var r = await _documentCollection.Indexes.ListAsync();
        foreach (var i in await r.ToListAsync())
        {
            var indexName = i["name"].ToString();
            if (!string.IsNullOrEmpty(indexName) && indexName.StartsWith(name))
            {
                await _documentCollection.Indexes.DropOneAsync(indexName);
            }
        }
    }

    /// <summary>
    ///     Finds all documents matching a given expression
    /// </summary>
    /// <param name="session">Session object to manage the transaction</param>
    /// <param name="filterDefinition">The filter definition</param>
    /// <param name="sort">Sort definition</param>
    /// <param name="skip">The number of documents to skip in the query</param>
    /// <param name="take">The maximal amount of documents to return. The skip is applied before the limit restriction</param>
    /// <returns>Returns a cursor</returns>
    public async Task<ICollection<TDocument>> FindManyAsync(IOctoSession session,
        FilterDefinition<TDocument> filterDefinition,
        SortDefinition<TDocument>? sort = null, int? skip = null, int? take = null)
    {
        try
        {
            var cursor = await _documentCollection.FindAsync(((IOctoSessionInternal)session).SessionHandle,
                filterDefinition, new FindOptions<TDocument>
                {
                    Sort = sort,
                    Skip = skip,
                    Limit = take
                });
            return await cursor.ToListAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
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
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
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
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
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

    public async Task ReplaceByIdAsync<TField>(IOctoSession session, TField id, TDocument document)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var result = await _documentCollection.ReplaceOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filter, document);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero<TField, TDocument>(result.MatchedCount, id);
        }
        catch (MongoWriteException ex)
        {
            Logger.Error(ex);
            HandleWriteException<TDocument>(ex);
        }
    }

    public async Task UpdateOneAsync<TField>(IOctoSession session, TField id,
        UpdateDefinition<TDocument> updateDefinition)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var result = await _documentCollection.UpdateOneAsync(((IOctoSessionInternal)session).SessionHandle,
                filter, updateDefinition);
            ThrowIfNotAcknowledged(result.IsAcknowledged);
            ThrowIfMatchedCountZero<TField, TDocument>(result.MatchedCount, id);
        }
        catch (MongoWriteException ex)
        {
            Logger.Error(ex);
            HandleWriteException<TDocument>(ex);
        }
    }

    public async Task<TDerived?> DocumentAsync<TDerived, TField>(IOctoSession session, TField id)
        where TDerived : TDocument, new()
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var s = (TDerived)await (await _documentCollection.FindAsync(
                    ((IOctoSessionInternal)session).SessionHandle, filter))
                .SingleOrDefaultAsync();
            return s;
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }

    public async Task<TDocument?> DocumentAsync<TField>(IOctoSession session, TField id)
    {
        try
        {
            return await DocumentAsync<TDocument, TField>(session, id);
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
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
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }
    
    public async Task TryDeleteOneAsync<TField>(IOctoSession session, TField id)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var deleteResult = await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }

    public async Task DeleteOneAsync<TField>(IOctoSession session, TField id)
    {
        try
        {
            var filter = Builders<TDocument>.Filter.BuildIdFilter(id);
            var deleteResult = await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero(deleteResult.DeletedCount, id);
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }

    public async Task DeleteOneAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, expression);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero<TDocument>(deleteResult.DeletedCount, expression);
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }
    
    public async Task DeleteOneAsync(IOctoSession session, FilterDefinition<TDocument> filter)
    {
        try
        {
            var deleteResult =
                await _documentCollection.DeleteOneAsync(((IOctoSessionInternal)session).SessionHandle, filter);
            ThrowIfNotAcknowledged(deleteResult.IsAcknowledged);
            ThrowIfMatchedCountZero<TDocument>(deleteResult.DeletedCount, filter);
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }

    public async Task DeleteManyAsync<TField>(IOctoSession session, IEnumerable<TField> ids)
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
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
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
            ThrowIfMatchedCountZero<TDocument>(deleteResult.DeletedCount, expression);
        }
        catch (MongoException e)
        {
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }

    public async Task<IBulkImportResult> BulkImportAsync(IOctoSession session, IEnumerable<TDocument> documentList)
    {
        try
        {
            var listWrites = new List<WriteModel<TDocument>>();
            foreach (var v in documentList)
            {
                listWrites.Add(new InsertOneModel<TDocument>(v));
            }

            var result =
                await _documentCollection.BulkWriteAsync(((IOctoSessionInternal)session).SessionHandle, listWrites);
            return new BulkImportResult(result);
        }
        catch (MongoBulkWriteException e)
        {
            throw new OperationFailedException($"Bulk import failed: {e.Message}", e);
        }
    }


    public IUpdateStream<TDocument> Subscribe(UpdateTypes updateTypes,
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

    #region Insert operations

    public async Task InsertMultipleAsync(IOctoSession session, IEnumerable<TDocument> documentCollection)
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
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }


    public async Task InsertAsync(IOctoSession session, TDocument document)
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
            Logger.Error(e);
            throw new OperationFailedException(e.Message, e);
        }
    }

    #endregion Insert operations


    #region Exception helpers

    private void HandleWriteException<T>(MongoWriteException ex)
    {
        if (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateKeyException($"Error adding item of type {nameof(T)}", typeof(T), ex);
        }

        throw new OperationFailedException("Operation was not completed.", ex);
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
    private void ThrowIfMatchedCountZero<TField, T>(long matchedCount, TField id)
    {
        if (matchedCount == 0)
        {
            var message = $"Operation failed because ID '{id}' is not existing for document type {nameof(T)}.";
            throw new EntityNotFoundException(message);
        }
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void ThrowIfMatchedCountZero<T>(long matchedCount, Expression<Func<TDocument, bool>> expression)
    {
        if (matchedCount == 0)
        {
            var message =
                $"Operation failed because filter '{expression}' did not match any documents for type {nameof(T)}.";
            throw new EntityNotFoundException(message);
        }
    }
    
    private void ThrowIfMatchedCountZero<T>(long matchedCount, FilterDefinition<TDocument> filter)
    {
        if (matchedCount == 0)
        {
            var message =
                $"Operation failed because filter '{filter}' did not match any documents for type {nameof(T)}.";
            throw new EntityNotFoundException(message);
        }
    }

    private void ThrowIfMatchedCountZero<TField>(long matchedCount, TField idField)
    {
        if (matchedCount == 0)
        {
            var message =
                $"Operation failed because filter '{idField}' did not match any documents for type {nameof(TDocument)}.";
            throw new EntityNotFoundException(message);
        }
    }
    
    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void ThrowIfMatchedCountZero(long matchedCount)
    {
        if (matchedCount == 0)
        {
            var message = "Operation may failed because no data matched.";
            throw new EntityNotFoundException(message);
        }
    }

    #endregion Exception helpers
}