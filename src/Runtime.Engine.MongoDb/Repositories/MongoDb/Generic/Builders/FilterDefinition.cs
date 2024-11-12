using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class FilterDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> input,
    string @as,
    AggregateExpressionDefinition<TSource, TResult> condition)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly string _as = Ensure.IsNotNull(@as, nameof(@as));

    private readonly AggregateExpressionDefinition<TSource, TResult> _condition =
        Ensure.IsNotNull(condition, nameof(condition));

    private readonly AggregateExpressionDefinition<TSource, TResult> _input = Ensure.IsNotNull(input, nameof(input));

    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var inputField = _input.Render(args);
        var conditionField = _condition.Render(args);
        var bsonDocument = new BsonDocument
        {
            { "input", inputField },
            { "as", _as },
            { "cond", conditionField }
        };
        return new BsonDocument("$filter", bsonDocument);
    }
}