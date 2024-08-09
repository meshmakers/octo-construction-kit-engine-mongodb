using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class GtDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult>[] fields)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult>[] _fields = Ensure.IsNotNull(fields, nameof(fields));


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        BsonArray array = new BsonArray();
        foreach (var fieldDefinition in _fields)
        {
            var value = fieldDefinition.Render(sourceSerializer, serializerRegistry, linqProvider);
            array.Add(value);
        }
        return new BsonDocument("$gt", array);
    }
}