using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ConstantDefinition<TSource, TResult>(
    object value)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly object _value = Ensure.IsNotNull(value, nameof(value));

    public override BsonValue Render(RenderArgs<TSource> args)
    {
        return BsonValue.Create(_value);
    }
}