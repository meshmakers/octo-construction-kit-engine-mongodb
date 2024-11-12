using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ArrayDefinition<TSource, TResult>(BsonArray array)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly BsonArray _array = Ensure.IsNotNull(array, nameof(array));

    public override BsonValue Render(RenderArgs<TSource> args)
    {
        return _array;
    }
}