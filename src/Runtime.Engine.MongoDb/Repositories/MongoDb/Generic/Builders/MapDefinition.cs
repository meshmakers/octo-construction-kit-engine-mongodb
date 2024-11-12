using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class MapDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> input,
    string @as,
    AggregateExpressionDefinition<TSource, TResult> @in)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly string _as = Ensure.IsNotNull(@as, nameof(@as));
    private readonly AggregateExpressionDefinition<TSource, TResult> _in = Ensure.IsNotNull(@in, nameof(@in));
    private readonly AggregateExpressionDefinition<TSource, TResult> _input = Ensure.IsNotNull(input, nameof(input));


    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var inputValue = _input.Render(args);
        var inValue = _in.Render(args);
        var bsonDocument = new BsonDocument
        {
            { "input", inputValue },
            { "as", _as },
            { "in", inValue }
        };
        return new BsonDocument(new BsonDocument("$map", bsonDocument));
    }
}