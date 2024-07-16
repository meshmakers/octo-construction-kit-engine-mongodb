using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class SingleFieldProjectionDefinition<TSource, TResult>(FieldDefinition<TSource> field, AggregateExpressionDefinition<TSource, TResult> value)
    : ProjectionDefinition<TSource>
{
    private readonly FieldDefinition<TSource> _field = Ensure.IsNotNull(field, nameof(field));
    private readonly AggregateExpressionDefinition<TSource, TResult> _value = Ensure.IsNotNull(value, nameof(value));

    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer, IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var renderedField = _field.Render(sourceSerializer, serializerRegistry, linqProvider);
        var value = _value.Render(sourceSerializer, serializerRegistry, linqProvider);
        return new BsonDocument(renderedField.FieldName, value);
    }
}
