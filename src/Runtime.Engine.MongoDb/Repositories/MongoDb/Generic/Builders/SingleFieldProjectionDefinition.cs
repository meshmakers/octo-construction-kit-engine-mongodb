using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class SingleFieldProjectionDefinition<TSource, TResult>(
    FieldDefinition<TSource> field,
    AggregateExpressionDefinition<TSource, TResult> value)
    : ProjectionDefinition<TSource>
{
    private readonly FieldDefinition<TSource> _field = Ensure.IsNotNull(field, nameof(field));
    private readonly AggregateExpressionDefinition<TSource, TResult> _value = Ensure.IsNotNull(value, nameof(value));

    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var renderedField = _field.Render(args);
        var value = _value.Render(args);
        return new BsonDocument(renderedField.FieldName, value);
    }
}