using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class SortArrayDefinition<TSource, TResult>(
    FieldDefinition<TSource> input,
    BsonDocument sort)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly FieldDefinition<TSource> _input = Ensure.IsNotNull(input, nameof(input));
    private readonly BsonDocument _sort = Ensure.IsNotNull(sort, nameof(sort));


    public override BsonDocument Render(IBsonSerializer<TSource> sourceSerializer,
        IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        var inputField = _input.Render(sourceSerializer, serializerRegistry, linqProvider);
        var args = new BsonDocument
        {
            { "input", inputField.FieldName },
            { "sortBy", _sort }
        };
        return new BsonDocument("$sortArray", args);
    }
}