using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class AggregateExpressionFieldDefinition<TSource, TResult>(
    FieldDefinition<TSource> field,
    AggregateExpressionDefinition<TSource, TResult> value)
    : SetFieldDefinition<TSource>
{
    private readonly FieldDefinition<TSource> _field = Ensure.IsNotNull(field, nameof(field));
    private readonly AggregateExpressionDefinition<TSource, TResult> _value = Ensure.IsNotNull(value, nameof(value));

    public override BsonElement Render(RenderArgs<TSource> args)
    {
        var renderedField = _field.Render(args);
        var renderedValue = _value.Render(args);

        return new BsonElement(renderedField.FieldName, renderedValue);
    }
}