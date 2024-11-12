using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class DocumentDefinition<TSource, TResult>(BsonDocument document)
    : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly BsonDocument _document = Ensure.IsNotNull(document, nameof(document));

    public override BsonValue Render(RenderArgs<TSource> args)
    {
        return _document;
    }
}