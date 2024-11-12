using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class SortArrayDefinition<TSource, TResult>(
    FieldDefinition<TSource> input,
    BsonDocument sort)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly FieldDefinition<TSource> _input = Ensure.IsNotNull(input, nameof(input));
    private readonly BsonDocument _sort = Ensure.IsNotNull(sort, nameof(sort));


    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var inputField = _input.Render(args);
        var bsonDocument = new BsonDocument
        {
            { "input", inputField.FieldName },
            { "sortBy", _sort }
        };
        return new BsonDocument("$sortArray", bsonDocument);
    }
}