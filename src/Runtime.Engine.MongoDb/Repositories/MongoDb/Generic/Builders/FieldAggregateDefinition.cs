using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class FieldAggregateDefinition<TSource, TResult>(string fieldName)
    : AggregateExpressionDefinition<TSource, TResult>
{
    public override BsonValue Render(RenderArgs<TSource> args)
    {
        return new BsonString(fieldName);
    }
}