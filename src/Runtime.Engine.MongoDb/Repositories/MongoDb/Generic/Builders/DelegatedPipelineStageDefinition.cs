using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class DelegatedPipelineStageDefinition<TInput, TOutput>(
    string operatorName,
    Func<RenderArgs<TInput>, RenderedPipelineStageDefinition<TOutput>>
        renderer)
    : PipelineStageDefinition<TInput, TOutput>
{
    public override string OperatorName => operatorName;

    public override RenderedPipelineStageDefinition<TOutput> Render(RenderArgs<TInput> args)
    {
        return renderer(args);
    }
}