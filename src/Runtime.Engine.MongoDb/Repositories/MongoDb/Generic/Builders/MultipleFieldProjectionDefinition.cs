using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class MultipleFieldProjectionDefinition<TSource, TResult>(
    IEnumerable<ProjectionDefinition<TSource, TResult>> values)
    : ProjectionDefinition<TSource>
{
    public override BsonDocument Render(RenderArgs<TSource> args)
    {
        var bsonDocument = new BsonDocument();
        foreach (var aggregateExpressionDefinition in values)
        {
            var renderedFilter = aggregateExpressionDefinition.Render(args);
            bsonDocument.AddRange(renderedFilter.Document.Elements);
        }

        return new BsonDocument( bsonDocument);
    }
}
