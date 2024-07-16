using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class InFilterDefinition<TSource, TResult>(
    IEnumerable<AggregateExpressionDefinition<TSource, TResult>> values)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly IEnumerable<AggregateExpressionDefinition<TSource, TResult>> _values =
        Ensure.IsNotNull(values, nameof(values));

    public override BsonDocument Render(IBsonSerializer<TSource> documentSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var array = new BsonArray();
        foreach (var value in _values)
        {
            var renderedField = value.Render(documentSerializer, serializerRegistry, linqProvider);
            array.Add(renderedField);
        }

        return new BsonDocument("$in", array);
    }
}