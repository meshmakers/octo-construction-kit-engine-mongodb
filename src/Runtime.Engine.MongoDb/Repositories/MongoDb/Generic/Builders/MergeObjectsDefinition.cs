using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class MergeObjectsDefinition<TSource, TResult>(IEnumerable<AggregateExpressionDefinition<TSource, TResult>> values)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly List<AggregateExpressionDefinition<TSource, TResult>> _values = Ensure.IsNotNull(values, nameof(values)).ToList();


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var array = new BsonArray();
        foreach (var filter in _values)
        {
            var renderedFilter = filter.Render(sourceSerializer, serializerRegistry, linqProvider);
            array.Add(renderedFilter); 
        }
        
        return new BsonDocument( new BsonDocument("$mergeObjects", array));
    }
}