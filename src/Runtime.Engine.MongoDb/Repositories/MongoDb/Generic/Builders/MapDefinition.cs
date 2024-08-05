using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class MapDefinition<TSource, TResult>(AggregateExpressionDefinition<TSource, TResult> input,
    string @as, AggregateExpressionDefinition<TSource, TResult> @in)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _input = Ensure.IsNotNull(input, nameof(input));
    private readonly string _as = Ensure.IsNotNull(@as, nameof(@as));
    private readonly AggregateExpressionDefinition<TSource, TResult> _in = Ensure.IsNotNull(@in, nameof(@in));


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var inputValue = _input.Render(sourceSerializer, serializerRegistry, linqProvider);
        var inValue = _in.Render(sourceSerializer, serializerRegistry, linqProvider);
        var args = new BsonDocument
        {
            { "input", inputValue },
            { "as", _as },
            { "in", inValue }
        };
        return new BsonDocument( new BsonDocument("$map", args));
    }
}