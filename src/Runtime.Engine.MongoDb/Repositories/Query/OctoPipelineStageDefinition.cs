using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

public static class OctoPipelineStageDefinition
{
    public static PipelineStageDefinition<TInput, TInput> GeoNear<TInput, TCoordinates>(string attributeName,
        GeoJsonPoint<TCoordinates> point, double? minDistance, double? maxDistance)
        where TCoordinates : GeoJsonCoordinates
    {
        const string operatorName = "$geoNear";
        var stage = new OctoDelegatedPipelineStageDefinition<TInput, TInput>(
            operatorName,
            (s, sr, linqProvider) =>
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
                    bsonWriter.WriteString(attributeName);
                    bsonWriter.WriteName("near");
                    sr.GetSerializer<GeoJsonPoint<TCoordinates>>().Serialize(context, point);
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
                    sr.GetSerializer<TInput>());
            });
        
        return stage;
    }
}