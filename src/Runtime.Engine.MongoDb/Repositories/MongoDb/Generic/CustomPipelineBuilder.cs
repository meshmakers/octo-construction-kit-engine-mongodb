using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal static class CustomPipelineBuilder
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
        var stage = new CustomDelegatedPipelineStageDefinition<TInput, TOutput>(
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

    private sealed class
        CustomDelegatedPipelineStageDefinition<TInput, TOutput>(
            string operatorName,
            Func<RenderArgs<TInput>,
                RenderedPipelineStageDefinition<TOutput>> renderer)
        : PipelineStageDefinition<TInput, TOutput>
    {
        public override string OperatorName { get; } = operatorName;

        public override RenderedPipelineStageDefinition<TOutput> Render(RenderArgs<TInput> args)
        {
            return renderer(args);
        }
    }
}