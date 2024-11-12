using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal static class OctoPipelineStageBuilder
{
    /// <summary>
    ///     Creates a $match stage.
    /// </summary>
    /// <typeparam name="TInput">The type of the input documents.</typeparam>
    /// <param name="filter">The filter.</param>
    /// <returns>The stage.</returns>
    public static PipelineStageDefinition<TInput, BsonDocument> Match<TInput>(
        AggregateExpressionDefinition<TInput, BsonDocument> filter)
    {
        const string operatorName = "$match";
        var stage = new DelegatedPipelineStageDefinition<TInput, BsonDocument>(
            operatorName,
            args => new RenderedPipelineStageDefinition<BsonDocument>(operatorName,
                new BsonDocument(operatorName, filter.Render(args)), args.GetSerializer<BsonDocument>()));

        return stage;
    }

    public static PipelineStageDefinition<BsonDocument, BsonDocument> AddFields(
        ListSetFieldDefinitions<BsonDocument> newDocument)
    {
        const string operatorName = "$addFields";
        var stage = new OctoDelegatedPipelineStageDefinition<BsonDocument, BsonDocument>(
            operatorName,
            args =>
            {
                var renderedProjection = newDocument.Render(args);

                var document = new BsonDocument(operatorName, renderedProjection);
                return new RenderedPipelineStageDefinition<BsonDocument>(
                    operatorName,
                    document,
                    args.GetSerializer<BsonDocument>());
            });

        return stage;
    }

    public static PipelineStageDefinition<TInput, TInput> GeoNear<TInput, TCoordinates>(string attributeName,
        GeoJsonPoint<TCoordinates> point, double? minDistance, double? maxDistance)
        where TCoordinates : GeoJsonCoordinates
    {
        const string operatorName = "$geoNear";
        var stage = new OctoDelegatedPipelineStageDefinition<TInput, TInput>(
            operatorName,
            args =>
            {
                var document = new BsonDocument();
                using (var bsonWriter = new BsonDocumentWriter(document))
                {
                    var context = BsonSerializationContext.CreateRoot(bsonWriter);
                    bsonWriter.WriteStartDocument();
                    bsonWriter.WriteName(operatorName);
                    bsonWriter.WriteStartDocument();
                    bsonWriter.WriteName("spherical");
                    bsonWriter.WriteBoolean(true);
                    bsonWriter.WriteName("distanceField");
                    bsonWriter.WriteString(attributeName + "_distance");
                    bsonWriter.WriteName("near");
                    args.GetSerializer<GeoJsonPoint<TCoordinates>>().Serialize(context, point);
                    if (maxDistance.HasValue)
                    {
                        bsonWriter.WriteName("maxDistance");
                        bsonWriter.WriteDouble(maxDistance.Value);
                    }

                    if (minDistance.HasValue)
                    {
                        bsonWriter.WriteName("minDistance");
                        bsonWriter.WriteDouble(minDistance.Value);
                    }

                    bsonWriter.WriteEndDocument();
                    bsonWriter.WriteEndDocument();
                }

                return new RenderedPipelineStageDefinition<TInput>(
                    operatorName,
                    document,
                    args.GetSerializer<TInput>());
            });

        return stage;
    }
}