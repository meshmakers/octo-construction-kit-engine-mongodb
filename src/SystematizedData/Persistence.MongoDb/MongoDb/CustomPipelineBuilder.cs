using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

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
            (inputSerializer, sr, linqProvider) =>
            {
                var foreignSerializer = options.ForeignSerializer ??
                                        inputSerializer as IBsonSerializer<TForeignDocument> ??
                                        sr.GetSerializer<TForeignDocument>();
                var outputSerializer = options.ResultSerializer ??
                                       inputSerializer as IBsonSerializer<TOutput> ?? sr.GetSerializer<TOutput>();
                if (lookupPipeline != null)
                {
                    var lookupPipelineDocuments = new BsonArray(lookupPipeline.Render(foreignSerializer, sr, linqProvider).Documents);

                    return new RenderedPipelineStageDefinition<TOutput>(
                        operatorName, new BsonDocument(operatorName, new BsonDocument
                        {
                            { "from", foreignCollection.CollectionNamespace.CollectionName },
                            { "localField", localField.Render(inputSerializer, sr, linqProvider).FieldName },
                            { "foreignField", foreignField.Render(foreignSerializer, sr, linqProvider).FieldName },
                            { "pipeline", lookupPipelineDocuments },
                            { "as", @as.Render(outputSerializer, sr, linqProvider).FieldName }
                        }),
                        outputSerializer);
                }

                return new RenderedPipelineStageDefinition<TOutput>(
                    operatorName, new BsonDocument(operatorName, new BsonDocument
                    {
                        { "from", foreignCollection.CollectionNamespace.CollectionName },
                        { "localField", localField.Render(inputSerializer, sr, linqProvider).FieldName },
                        { "foreignField", foreignField.Render(foreignSerializer, sr, linqProvider).FieldName },
                        { "as", @as.Render(outputSerializer, sr, linqProvider).FieldName }
                    }),
                    outputSerializer);
            });

        return stage;
    }

    private sealed class
        CustomDelegatedPipelineStageDefinition<TInput, TOutput> : PipelineStageDefinition<TInput, TOutput>
    {
        private readonly Func<IBsonSerializer<TInput>, IBsonSerializerRegistry, LinqProvider,
            RenderedPipelineStageDefinition<TOutput>> _renderer;

        public CustomDelegatedPipelineStageDefinition(string operatorName,
            Func<IBsonSerializer<TInput>, IBsonSerializerRegistry, LinqProvider,
                RenderedPipelineStageDefinition<TOutput>> renderer)
        {
            OperatorName = operatorName;
            _renderer = renderer;
        }

        public override string OperatorName { get; }

        public override RenderedPipelineStageDefinition<TOutput> Render(IBsonSerializer<TInput> inputSerializer,
            IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
        {
            return _renderer(inputSerializer, serializerRegistry, linqProvider);
        }
    }
}
