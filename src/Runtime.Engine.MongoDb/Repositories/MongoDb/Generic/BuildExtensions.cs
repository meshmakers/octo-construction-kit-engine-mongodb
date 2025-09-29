using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal static class BuildExtensions
{
    /// <summary>
    /// Injects an existing FilterDefinition<TEntity> into a FilterDefinition<ChangeStreamDocument<TEntity>>
    /// by prefixing all field names with "fullDocument.".
    /// </summary>
    /// <typeparam name="TEntity">Die Entitätstyp.</typeparam>
    /// <param name="filter">Der ursprüngliche Filter.</param>
    /// <returns>Ein neuer Filter, der auf fullDocument angewendet wird.</returns>
    internal static FilterDefinition<TDocument> Inject<TDocument, TInnerDocument>(this FilterDefinitionBuilder<TDocument> @this, string fieldName, FilterDefinition<TInnerDocument> filter)
    {
        // Rendern des ursprünglichen Filters zu einem BsonDocument
        var renderArgs = new RenderArgs<TInnerDocument>(BsonSerializer.SerializerRegistry.GetSerializer<TInnerDocument>(), BsonSerializer.SerializerRegistry);
        var renderedFilter = filter.Render(renderArgs);

        // Rekursive Methode zum Prefixen der Feldnamen
        var prefixedFilter = PrefixFieldNames(renderedFilter, "fullDocument.");

        // Rückgabe des neuen Filters als FilterDefinition<ChangeStreamDocument<TEntity>>
        return new BsonDocumentFilterDefinition<TDocument>(prefixedFilter);
    }
    
    /// <summary>
    /// Rekursiv alle Feldnamen in einem BsonDocument mit dem angegebenen Prefix versehen.
    /// </summary>
    /// <param name="original">Das ursprüngliche BsonDocument.</param>
    /// <param name="prefix">Das Präfix, das vor jedem Feldnamen gesetzt wird.</param>
    /// <returns>Ein neues BsonDocument mit präfixierten Feldnamen.</returns>
    private static BsonDocument PrefixFieldNames(BsonDocument original, string prefix)
    {
        var modified = new BsonDocument();

        foreach (var element in original.Elements)
        {
            if (element.Name.StartsWith("$"))
            {
                // Operatoren wie $and, $or etc. beibehalten und deren inneren Filter prefixen
                if (element.Value.IsBsonArray)
                {
                    var array = element.Value.AsBsonArray;
                    var newArray = new BsonArray();
                    foreach (var item in array)
                    {
                        if (item.IsBsonDocument)
                        {
                            newArray.Add(PrefixFieldNames(item.AsBsonDocument, prefix));
                        }
                        else
                        {
                            newArray.Add(item);
                        }
                    }
                    modified.Add(element.Name, newArray);
                }
                else if (element.Value.IsBsonDocument)
                {
                    modified.Add(element.Name, PrefixFieldNames(element.Value.AsBsonDocument, prefix));
                }
                else
                {
                    modified.Add(element.Name, element.Value);
                }
            }
            else
            {
                // Normale Feldnamen prefixen
                if (element.Value.IsBsonDocument)
                {
                    modified.Add(prefix + element.Name, PrefixFieldNames(element.Value.AsBsonDocument, prefix));
                }
                else
                {
                    modified.Add(prefix + element.Name, element.Value);
                }
            }
        }

        return modified;
    }
    
    /// <summary>
    /// Creates a filter for the id field.
    /// </summary>
    /// <param name="this"></param>
    /// <param name="id"></param>
    /// <typeparam name="TDocument"></typeparam>
    /// <typeparam name="TField"></typeparam>
    /// <returns></returns>
    internal static FilterDefinition<TDocument> BuildIdFilter<TDocument, TField>(
        this FilterDefinitionBuilder<TDocument> @this, TField id)
    {
        return @this.Eq(Constants.IdField, id);
    }
    
    /// <summary>
    /// Creates a filter for the RtAssociations
    /// </summary>
    internal static FilterDefinition<TDocument> BuildAssociationFilter<TDocument>(
        this FilterDefinitionBuilder<TDocument> @this, RtAssociation association)
    {
        return @this.And(
            @this.Eq("associationRoleId", association.AssociationRoleId),
            @this.Eq("originRtId", association.OriginRtId),
            @this.Eq("targetRtId", association.TargetRtId));
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
            OctoPipelineStageBuilder.Lookup(
                foreignCollection,
                localField,
                foreignField,
                @as,
                lookupPipeline,
                options));
    }
}
