using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class NullDefinition<TSource, TResult> : AggregateExpressionDefinition<TSource, TResult>
{
    public override BsonValue Render(RenderArgs<TSource> args)
    {
        return BsonNull.Value;
    }
}