using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class NullDefinition<TSource, TResult>: AggregateExpressionDefinition<TSource, TResult>
{
    public override BsonValue Render(IBsonSerializer<TSource> sourceSerializer, IBsonSerializerRegistry serializerRegistry,
        LinqProvider linqProvider)
    {
        return BsonNull.Value;
    }
}