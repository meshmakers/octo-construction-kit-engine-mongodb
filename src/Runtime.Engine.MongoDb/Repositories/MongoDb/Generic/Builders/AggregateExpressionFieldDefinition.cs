using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class AggregateExpressionFieldDefinition<TSource, TResult>(
    FieldDefinition<TSource> field,
    AggregateExpressionDefinition<TSource, TResult> value)
    : SetFieldDefinition<TSource>
{
    private readonly FieldDefinition<TSource> _field = Ensure.IsNotNull(field, nameof(field));
    private readonly AggregateExpressionDefinition<TSource, TResult> _value = Ensure.IsNotNull(value, nameof(value));
    
    public override BsonElement Render(IBsonSerializer<TSource> documentSerializer, IBsonSerializerRegistry serializerRegistry,
        LinqProvider linqProvider)
    {
        var renderedField = _field.Render(documentSerializer, serializerRegistry, linqProvider);
        var renderedValue = _value.Render(documentSerializer, serializerRegistry, linqProvider);
        
        return new BsonElement(renderedField.FieldName, renderedValue);
    }
}