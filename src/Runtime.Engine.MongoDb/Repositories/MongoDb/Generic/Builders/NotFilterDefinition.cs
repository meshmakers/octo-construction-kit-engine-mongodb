using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class NotFilterDefinition<TSource, TResult>(
    IEnumerable<AggregateExpressionDefinition<TSource, TResult>> values)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly IEnumerable<AggregateExpressionDefinition<TSource, TResult>> _values =
        Ensure.IsNotNull(values, nameof(values));

    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var array = new BsonArray();
        foreach (var value in _values)
        {
            var renderedField = value.Render(args);
            array.Add(renderedField);
        }

        return new BsonDocument("$not", array);
    }
}