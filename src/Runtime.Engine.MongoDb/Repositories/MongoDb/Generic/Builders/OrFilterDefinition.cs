using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class OrFilterDefinition<TSource, TResult>(IEnumerable<AggregateExpressionDefinition<TSource, TResult>> values)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly List<AggregateExpressionDefinition<TSource, TResult>> _filters = Ensure.IsNotNull(values, nameof(values)).ToList();

    public override BsonDocument Render(IBsonSerializer<TSource> documentSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        if (_filters.Count == 0)
        {
            return new BsonDocument("$or", new BsonArray(0));
        }

        var array = new BsonArray();
        foreach (var filter in _filters)
        {
            var renderedFilter = filter.Render(documentSerializer, serializerRegistry, linqProvider);
            array.Add(renderedFilter); 
        }

        return new BsonDocument("$or", array);
    }

   
}