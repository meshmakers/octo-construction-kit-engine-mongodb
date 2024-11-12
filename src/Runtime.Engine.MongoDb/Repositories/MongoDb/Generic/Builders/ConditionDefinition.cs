using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ConditionDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult> condition,
    AggregateExpressionDefinition<TSource, TResult> trueDefinition,
    AggregateExpressionDefinition<TSource, TResult> falseDefinition) : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _condition =
        Ensure.IsNotNull(condition, nameof(condition));

    private readonly AggregateExpressionDefinition<TSource, TResult> _falseDefinition =
        Ensure.IsNotNull(falseDefinition, nameof(falseDefinition));

    private readonly AggregateExpressionDefinition<TSource, TResult> _trueDefinition =
        Ensure.IsNotNull(trueDefinition, nameof(trueDefinition));

    public override BsonValue Render(RenderArgs<TSource> args)
    {
        var conditionField = _condition.Render(args);
        var trueField = _trueDefinition.Render(args);
        var falseField = _falseDefinition.Render(args);
        var bsonArray = new BsonArray([conditionField, trueField, falseField]);
        return new BsonDocument("$cond", bsonArray);
    }
}