using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class AndFilterDefinition<TSource, TResult>(
    IEnumerable<AggregateExpressionDefinition<TSource, TResult>> values)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly List<AggregateExpressionDefinition<TSource, TResult>> _filters =
        Ensure.IsNotNull(values, nameof(values)).ToList();

    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        if (_filters.Count == 0)
        {
            return new BsonDocument("$and", new BsonArray(0));
        }

        var array = new BsonArray();
        foreach (var filter in _filters)
        {
            var renderedFilter = filter.Render(args);
            array.Add(renderedFilter);
        }

        return new BsonDocument("$and", array);
    }
}
