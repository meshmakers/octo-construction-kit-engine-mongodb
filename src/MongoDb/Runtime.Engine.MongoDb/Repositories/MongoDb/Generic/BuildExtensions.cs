using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal static class BuildExtensions
{
    internal static FilterDefinition<TDocument> BuildIdFilter<TDocument, TField>(
        this FilterDefinitionBuilder<TDocument> @this, TField id)
    {
        return @this.Eq(Constants.IdField, id);
    }

    /// <summary>
    ///     Appends a lookup stage to the pipeline.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <typeparam name="TForeignDocument">The type of the foreign collection documents.</typeparam>
    /// <typeparam name="TAsElement">The type of the as field elements.</typeparam>
    /// <typeparam name="TAs">The type of the as field.</typeparam>
    /// <typeparam name="TOutput">The type of the output documents.</typeparam>
    /// <param name="aggregate">The aggregate.</param>
    /// <param name="foreignCollection">The foreign collection.</param>
    /// <param name="localField">The local field.</param>
    /// <param name="foreignField">The foreign field.</param>
    /// ///
    /// <param name="lookupPipeline">The lookup pipeline.</param>
    /// <param name="as">The as field in <typeparamref name="TOutput" /> in which to place the results of the lookup pipeline.</param>
    /// <param name="options">The options.</param>
    /// <returns>The fluent aggregate interface.</returns>
    public static IAggregateFluent<TOutput> Lookup<TResult, TForeignDocument, TAsElement, TAs, TOutput>(
        this IAggregateFluent<TResult> aggregate,
        IMongoCollection<TForeignDocument> foreignCollection,
        FieldDefinition<TResult> localField,
        FieldDefinition<TForeignDocument> foreignField,
        PipelineDefinition<TForeignDocument, TAsElement>? lookupPipeline,
        FieldDefinition<TOutput, TAs> @as,
        AggregateLookupOptions<TForeignDocument, TOutput>? options = null)
        where TAs : IEnumerable<TAsElement>
    {
        Ensure.IsNotNull(aggregate, nameof(aggregate));
        return aggregate.AppendStage(
            CustomPipelineBuilder.Lookup(
                foreignCollection,
                localField,
                foreignField,
                @as,
                lookupPipeline,
                options));
    }
}