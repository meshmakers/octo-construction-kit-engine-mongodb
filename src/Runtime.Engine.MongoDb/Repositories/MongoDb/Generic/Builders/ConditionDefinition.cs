using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ConditionDefinition<TSource, TResult>( AggregateExpressionDefinition<TSource, TResult> condition,
    AggregateExpressionDefinition<TSource, TResult> trueDefinition,  AggregateExpressionDefinition<TSource, TResult> falseDefinition): AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _condition = Ensure.IsNotNull(condition, nameof(condition));
    private readonly AggregateExpressionDefinition<TSource, TResult> _trueDefinition = Ensure.IsNotNull(trueDefinition, nameof(trueDefinition));
    private readonly AggregateExpressionDefinition<TSource, TResult> _falseDefinition = Ensure.IsNotNull(falseDefinition, nameof(falseDefinition));

    public override BsonValue Render(IBsonSerializer<TSource> sourceSerializer, IBsonSerializerRegistry serializerRegistry,
        LinqProvider linqProvider)
    {
        var conditionField = _condition.Render(sourceSerializer, serializerRegistry, linqProvider);
        var trueField = _trueDefinition.Render(sourceSerializer, serializerRegistry, linqProvider);
        var falseField = _falseDefinition.Render(sourceSerializer, serializerRegistry, linqProvider);
        var args = new BsonArray(new[] { conditionField, trueField, falseField });
        return new BsonDocument("$cond", args);
    }
}