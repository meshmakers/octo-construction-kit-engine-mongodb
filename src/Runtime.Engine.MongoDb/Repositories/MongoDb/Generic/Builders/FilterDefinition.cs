using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class FilterDefinition<TSource, TResult>(AggregateExpressionDefinition<TSource, TResult> input, string @as, AggregateExpressionDefinition<TSource, TResult> condition)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly string _as = Ensure.IsNotNull(@as, nameof(@as));
    private readonly AggregateExpressionDefinition<TSource, TResult> _input = Ensure.IsNotNull(input, nameof(input));
    private readonly AggregateExpressionDefinition<TSource, TResult> _condition = Ensure.IsNotNull(condition, nameof(condition));

    public override BsonDocument Render(IBsonSerializer<TSource> documentSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {

        var inputField = _input.Render(documentSerializer, serializerRegistry, linqProvider);
        var conditionField = _condition.Render(documentSerializer, serializerRegistry, linqProvider);
        var args = new BsonDocument
        {
            { "input", inputField },
            { "as", _as },
            { "cond", conditionField }
        };
        return new BsonDocument("$filter", args);
    }

   
}