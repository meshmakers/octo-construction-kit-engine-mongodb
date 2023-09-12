using System.Linq.Expressions;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

public interface IDatabaseCollection<TDocument> : ICkDatabaseCollection<TDocument> where TDocument : class, new()
{
    Task CreateAscendingIndexAsync(string name, IEnumerable<string> fields);
    Task CreateTextIndexAsync(string name, string language, IEnumerable<CkIndexFields> fields);
    Task DropIndexAsync(string name);


    Task<ICollection<TDocument>> FindManyAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition,
        SortDefinition<TDocument>? sort = null, int? skip = null, int? take = null);




    Task InsertMultipleAsync(IOctoSession session, IEnumerable<TDocument> documentCollection);


    Task UpdateOneAsync<TField>(IOctoSession session, TField id, UpdateDefinition<TDocument> updateDefinition);
    Task<TDocument?> DocumentAsync<TField>(IOctoSession session, TField id);

    Task<TDerived?> DocumentAsync<TDerived, TField>(IOctoSession session, TField id)
        where TDerived : TDocument, new();

    Task<IEnumerable<TDocument>> GetAsync(IOctoSession session, int? skip = null, int? take = null);

    Task DeleteOneAsync(IOctoSession session, FilterDefinition<TDocument> filter);
    Task DeleteOneAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);

    Task DeleteManyAsync<TField>(IOctoSession session, IEnumerable<TField> ids);
    Task DeleteManyAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);


    IUpdateStream<TDocument> Subscribe(UpdateTypes updateTypes,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentFilterFunc = null,
        Func<FilterDefinition<ChangeStreamDocument<TDocument>>?>? documentBeforeFilterFunc = null,
        CancellationToken cancellationToken = default);

    IAggregateFluent<TDocument> Aggregate(IOctoSession session);

    IAsyncCursor<TOutput> Aggregate<TOutput>(IOctoSession session,
        PipelineDefinition<TDocument, TOutput> pipelineDefinition);

    Task<long> GetTotalCountAsync(IOctoSession session);
    Task<long> GetTotalCountAsync(IOctoSession session, FilterDefinition<TDocument> filterDefinition);
    Task<long> GetTotalCountAsync(IOctoSession session, Expression<Func<TDocument, bool>> expression);

    IMongoCollection<TDocument> GetMongoCollection();
}
