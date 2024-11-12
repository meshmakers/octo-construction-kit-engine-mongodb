using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class NeqDefinition<TSource, TResult>(
    AggregateExpressionDefinition<TSource, TResult>[] fields)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult>[] _fields =
        Ensure.IsNotNull(fields, nameof(fields));


    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var array = new BsonArray();
        foreach (var fieldDefinition in _fields)
        {
            var value = fieldDefinition.Render(args);
            array.Add(value);
        }

        return new BsonDocument("$ne", array);
    }
}