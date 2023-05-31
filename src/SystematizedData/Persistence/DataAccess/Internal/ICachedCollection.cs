using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface ICachedCollection<TDocument> where TDocument : class, new()
{
    Task CreateAscendingIndexAsync(string name, IEnumerable<string> fields);
    Task CreateTextIndexAsync(string name, string language, IEnumerable<CkIndexFields> fields);
    Task DropIndexAsync(string name);

    Task<TDocument> FindSingleOrDefaultAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);

    Task<ICollection<TDocument>> FindManyAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition,
        SortDefinition<TDocument> sort = null, int? skip = null, int? take = null);

    Task<ICollection<TDocument>> FindManyAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression,
        int? skip = null, int? limit = null);


    Task InsertMultipleAsync(IOctoSession session, IEnumerable<TDocument> documentCollection);
    Task InsertAsync(IOctoSession session, TDocument document);

    Task ReplaceByIdAsync<TField>(IOctoSession session, TField id, TDocument document);
    Task UpdateOneAsync<TField>(IOctoSession session, TField id, UpdateDefinition<TDocument> updateDefinition);
    Task<TDocument?> DocumentAsync<TField>(IOctoSession session, TField id);

    Task<TDerived?> DocumentAsync<TDerived, TField>(IOctoSession session, TField id)
        where TDerived : TDocument, new();

    Task<IEnumerable<TDocument>> GetAsync(IOctoSession session, int? skip = null, int? take = null);
    Task DeleteOneAsync<TField>(IOctoSession session, TField id);
    Task DeleteOneAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);

    Task DeleteManyAsync<TField>(IOctoSession session, IEnumerable<TField> ids);
    Task DeleteManyAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);
    Task<BulkImportResult> BulkImportAsync(IOctoSession session, IEnumerable<TDocument> documentList);

    IUpdateStream<TDocument> Subscribe(UpdateTypes updateTypes,
        Func<PipelineDefinition<ChangeStreamDocument<TDocument>, ChangeStreamDocument<TDocument>>,
            PipelineDefinition<ChangeStreamDocument<TDocument>, ChangeStreamDocument<TDocument>>> pipelineFunc,
        CancellationToken cancellationToken = default);

    IAggregateFluent<TDocument> Aggregate(IOctoSession session);

    IAsyncCursor<TOutput> Aggregate<TOutput>(IOctoSession session,
        PipelineDefinition<TDocument, TOutput> pipelineDefinition);

    Task<long> GetTotalCountAsync(IOctoSession session);
    Task<long> GetTotalCountAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition);
    Task<long> GetTotalCountAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);

    IMongoCollection<TDocument> GetMongoCollection();
}
