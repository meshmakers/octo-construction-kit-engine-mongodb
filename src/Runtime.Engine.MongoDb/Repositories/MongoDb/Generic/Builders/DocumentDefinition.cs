using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class DocumentDefinition<TSource, TResult>(BsonDocument document): AggregateExpressionDefinition<TSource, TResult>
{
    private readonly BsonDocument _document = Ensure.IsNotNull(document, nameof(document));

    public override BsonValue Render(IBsonSerializer<TSource> sourceSerializer, IBsonSerializerRegistry serializerRegistry,
        LinqProvider linqProvider)
    {
        return _document;
    }
}