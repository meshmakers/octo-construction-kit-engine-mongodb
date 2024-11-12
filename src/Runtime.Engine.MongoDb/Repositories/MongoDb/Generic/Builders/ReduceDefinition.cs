using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ReduceDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> input,
    AggregateExpressionDefinition<TSource, TResult> initialValue,
    AggregateExpressionDefinition<TSource, TResult> @in)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _in = Ensure.IsNotNull(@in, nameof(@in));

    private readonly AggregateExpressionDefinition<TSource, TResult> _initialValue =
        Ensure.IsNotNull(initialValue, nameof(initialValue));

    private readonly AggregateExpressionDefinition<TSource, TResult> _input = Ensure.IsNotNull(input, nameof(input));


    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var input = _input.Render(args);
        var initial = _initialValue.Render(args);
        var @in = _in.Render(args);
        var bsonDocument = new BsonDocument
        {
            { "input", input },
            { "initialValue", initial },
            { "in", @in }
        };
        return new BsonDocument("$reduce", bsonDocument);
    }
}