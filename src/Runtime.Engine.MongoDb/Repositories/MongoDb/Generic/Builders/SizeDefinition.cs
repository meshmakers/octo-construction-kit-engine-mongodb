using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class SizeDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> field)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _field = Ensure.IsNotNull(field, nameof(field));


    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var field = _field.Render(args);
        return new BsonDocument("$size", field);
    }
}