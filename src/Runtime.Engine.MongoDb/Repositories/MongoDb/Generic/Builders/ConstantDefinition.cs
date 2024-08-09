using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ConstantDefinition<TSource, TResult>(
    object value)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly object _value = Ensure.IsNotNull(value, nameof(value));

    public override BsonValue Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        return BsonValue.Create(_value);
    }
}