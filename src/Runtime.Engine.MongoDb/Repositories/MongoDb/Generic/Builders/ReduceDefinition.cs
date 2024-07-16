using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ReduceDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> input,
    AggregateExpressionDefinition<TSource, TResult> initialValue,
    AggregateExpressionDefinition<TSource, TResult> @in)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _input = Ensure.IsNotNull(input, nameof(input));
    private readonly AggregateExpressionDefinition<TSource, TResult> _initialValue = Ensure.IsNotNull(initialValue, nameof(initialValue));
    private readonly AggregateExpressionDefinition<TSource, TResult> _in = Ensure.IsNotNull(@in, nameof(@in));


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var input = _input.Render(sourceSerializer, serializerRegistry, linqProvider);
        var initial = _initialValue.Render(sourceSerializer, serializerRegistry, linqProvider);
        var @in = _in.Render(sourceSerializer, serializerRegistry, linqProvider);
        var args = new BsonDocument
        {
            { "input", input },
            { "initialValue", initial },
            { "in", @in }
        };
        return new BsonDocument("$reduce", args);
    }
}