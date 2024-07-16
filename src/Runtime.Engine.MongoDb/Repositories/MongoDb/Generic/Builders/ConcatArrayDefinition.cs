using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ConcatArrayDefinition<TSource, TResult>(
    BsonArray values)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly BsonArray _values = Ensure.IsNotNull(values, nameof(values));


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        return new BsonDocument( new BsonDocument("$concatArrays", _values));
    }
}