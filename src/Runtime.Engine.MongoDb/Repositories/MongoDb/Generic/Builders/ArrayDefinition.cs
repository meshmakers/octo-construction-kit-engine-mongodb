using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ArrayDefinition<TSource, TResult>(BsonArray array): AggregateExpressionDefinition<TSource, TResult>
{
    private readonly BsonArray _array = Ensure.IsNotNull(array, nameof(array));

    public override BsonValue Render(IBsonSerializer<TSource> sourceSerializer, IBsonSerializerRegistry serializerRegistry,
        LinqProvider linqProvider)
    {
        return _array;
    }
}