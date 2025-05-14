using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class IfNullDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult>[] arrays)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult>[] _arrays =
        Ensure.IsNotNull(arrays, nameof(arrays));


    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var array = new BsonArray();
        foreach (var filter in _arrays)
        {
            var renderedFilter = filter.Render(args);
            array.Add(renderedFilter);
        }

        return new BsonDocument(new BsonDocument("$ifNull", array));
    }
}
