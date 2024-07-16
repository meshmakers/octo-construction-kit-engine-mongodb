using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class DelegatedPipelineStageDefinition<TInput, TOutput>(
    string operatorName,
    Func<IBsonSerializer<TInput>, IBsonSerializerRegistry, LinqProvider, RenderedPipelineStageDefinition<TOutput>>
        renderer)
    : PipelineStageDefinition<TInput, TOutput>
{
    public override string OperatorName => operatorName;

    public override RenderedPipelineStageDefinition<TOutput> Render(IBsonSerializer<TInput> inputSerializer, IBsonSerializerRegistry serializerRegistry, LinqProvider linqProvider)
    {
        return renderer(inputSerializer, serializerRegistry, linqProvider);
    }
}