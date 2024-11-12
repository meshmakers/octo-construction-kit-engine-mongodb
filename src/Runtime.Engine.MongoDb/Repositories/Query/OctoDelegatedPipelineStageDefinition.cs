using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal sealed class OctoDelegatedPipelineStageDefinition<TInput, TOutput>(
    string operatorName,
    Func<RenderArgs<TInput>, RenderedPipelineStageDefinition<TOutput>>
        renderer)
    : PipelineStageDefinition<TInput, TOutput>
{
    public override string OperatorName { get; } = operatorName;

    public override RenderedPipelineStageDefinition<TOutput> Render(RenderArgs<TInput> args)
    {
        return renderer(args);
    }
}