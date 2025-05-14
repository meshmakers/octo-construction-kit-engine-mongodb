using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal static class OctoPipelineStageBuilder
{
    public static PipelineStageDefinition<TInput, TOutput> Lookup<TInput, TForeignDocument, TAsElement, TAs, TOutput>(
        IMongoCollection<TForeignDocument> foreignCollection,
        FieldDefinition<TInput> localField,
        FieldDefinition<TForeignDocument> foreignField,
        FieldDefinition<TOutput, TAs> @as,
        PipelineDefinition<TForeignDocument, TAsElement>? lookupPipeline,
        AggregateLookupOptions<TForeignDocument, TOutput>? options = null)
        where TAs : IEnumerable<TAsElement>
    {
        Ensure.IsNotNull(foreignCollection, nameof(foreignCollection));
        Ensure.IsNotNull(localField, nameof(localField));
        Ensure.IsNotNull(foreignField, nameof(foreignField));
        Ensure.IsNotNull(@as, nameof(@as));

        options ??= new AggregateLookupOptions<TForeignDocument, TOutput>();
        const string operatorName = "$lookup";
        var stage = new DelegatedPipelineStageDefinition<TInput, TOutput>(
            operatorName,
            args =>
            {
                var foreignSerializer = options.ForeignSerializer ??
                                        args.DocumentSerializer as IBsonSerializer<TForeignDocument> ??
                                        args.GetSerializer<TForeignDocument>();
                var outputSerializer = options.ResultSerializer ??
                                       args.DocumentSerializer as IBsonSerializer<TOutput> ??
                                       args.GetSerializer<TOutput>();
                if (lookupPipeline != null)
                {
                    var lookupPipelineDocuments = new BsonArray(lookupPipeline
                        .Render(new RenderArgs<TForeignDocument>(foreignSerializer, args.SerializerRegistry)).Documents);

                    return new RenderedPipelineStageDefinition<TOutput>(
                        operatorName, new BsonDocument(operatorName, new BsonDocument
                        {
                            { "from", foreignCollection.CollectionNamespace.CollectionName },
                            { "localField", localField.Render(args).FieldName },
                            {
                                "foreignField",
                                foreignField.Render(new RenderArgs<TForeignDocument>(foreignSerializer,
                                    args.SerializerRegistry)).FieldName
                            },
                            { "pipeline", lookupPipelineDocuments },
                            {
                                "as",
                                @as.Render(new RenderArgs<TOutput>(outputSerializer, args.SerializerRegistry)).FieldName
                            }
                        }),
                        outputSerializer);
                }

                return new RenderedPipelineStageDefinition<TOutput>(
                    operatorName, new BsonDocument(operatorName, new BsonDocument
                    {
                        { "from", foreignCollection.CollectionNamespace.CollectionName },
                        { "localField", localField.Render(args).FieldName },
                        {
                            "foreignField",
                            foreignField.Render(new RenderArgs<TForeignDocument>(foreignSerializer,
                                args.SerializerRegistry)).FieldName
                        },
                        {
                            "as",
                            @as.Render(new RenderArgs<TOutput>(outputSerializer, args.SerializerRegistry)).FieldName
                        }
                    }),
                    outputSerializer);
            });

        return stage;
    }

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

    public static PipelineStageDefinition<TInput, TOutput> AddFields<TInput, TOutput>(
        ListSetFieldDefinitions<TInput> newDocument)
    {
        const string operatorName = "$addFields";
        var stage = new OctoDelegatedPipelineStageDefinition<TInput, TOutput>(
            operatorName,
            args =>
            {
                var renderedProjection = newDocument.Render(args);

                var document = new BsonDocument(operatorName, renderedProjection);
                return new RenderedPipelineStageDefinition<TOutput>(
                    operatorName,
                    document,
                    args.GetSerializer<TOutput>());
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
