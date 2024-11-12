using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ExpressionFilterDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> filter) : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _filter = Ensure.IsNotNull(filter, nameof(filter));

    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var renderedFilter = _filter.Render(args);
        return new BsonDocument("$expr", renderedFilter);
    }
}