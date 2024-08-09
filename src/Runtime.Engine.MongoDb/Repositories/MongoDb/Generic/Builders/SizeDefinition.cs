using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class SizeDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> field)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _field = Ensure.IsNotNull(field, nameof(field));


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var field = _field.Render(sourceSerializer, serializerRegistry, linqProvider);
        return new BsonDocument("$size", field);
    }
}