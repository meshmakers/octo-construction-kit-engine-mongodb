using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal sealed class ExpressionFilterDefinition<TSource, TResult>(AggregateExpressionDefinition<TSource, TResult> filter) : AggregateExpressionDefinition<TSource, TResult>
{
    private readonly AggregateExpressionDefinition<TSource, TResult> _filter = Ensure.IsNotNull(filter, nameof(filter));

    public override BsonDocument Render(IBsonSerializer<TSource> documentSerializer, IBsonSerializerRegistry serializerRegistry,
        LinqProvider linqProvider)
    {
        var renderedFilter = _filter.Render(documentSerializer, serializerRegistry, linqProvider);
        return new BsonDocument("$expr", renderedFilter);

    }
}