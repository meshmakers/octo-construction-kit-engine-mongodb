using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

internal static class OctoBuilder<TSource, TResult>
{
    public static FieldBuilder<TSource, TResult> Fields => new();
    public static AggregationOperatorsBuilder<TSource, TResult> AggregateOperators => new();
    public static ProjectionBuilder<TSource, TResult> Projection => new();
}

internal class FieldBuilder<TSource, TResult>
{
    public ListSetFieldDefinitions<TSource> Set(FieldDefinition<TSource, TResult> field, AggregateExpressionDefinition<TSource, TResult> value)
    {
        var setFieldDefinition = new AggregateExpressionFieldDefinition<TSource, TResult>(field, value);
        return new ListSetFieldDefinitions<TSource>(new[] { setFieldDefinition });
    }
}

internal class AggregationOperatorsBuilder<TSource, TResult>
{
    public AggregateExpressionDefinition<TSource, TResult> Document(BsonDocument document)
    {
        return new DocumentDefinition<TSource, TResult>(document);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> Null()
    {
        return new NullDefinition<TSource, TResult>();
    }
    
    /// <summary>
    /// Creates an and filter.
    /// </summary>
    /// <param name="fields">Filter expressions.</param>
    /// <returns>An and filter.</returns>
    public AggregateExpressionDefinition<TSource, TResult> Neq(params AggregateExpressionDefinition<TSource, TResult>[] fields)
    {
        return new NeqDefinition<TSource,TResult>(fields);
    }
    
    /// <summary>
    /// Creates an and filter.
    /// </summary>
    /// <param name="input">The input field.</param>
    /// <param name="as">As field.</param>
    /// <param name="condition">As field.</param>
    /// <returns>An and filter.</returns>
    public AggregateExpressionDefinition<TSource, TResult> Filter(AggregateExpressionDefinition<TSource, TResult> input, string @as, AggregateExpressionDefinition<TSource, TResult> condition)
    {
        return new FilterDefinition<TSource,TResult>(input, @as, condition);
    }
    
    /// <summary>
    /// Creates an and filter.
    /// </summary>
    /// <param name="filters">The filters.</param>
    /// <returns>An and filter.</returns>
    public AggregateExpressionDefinition<TSource, TResult> And(params AggregateExpressionDefinition<TSource, TResult>[] filters)
    {
        return new AndFilterDefinition<TSource,TResult>(filters);
    }
    
    /// <summary>
    /// Creates an or filter.
    /// </summary>
    /// <param name="filters">The filters.</param>
    /// <returns>An and filter.</returns>
    public AggregateExpressionDefinition<TSource, TResult> Or(params AggregateExpressionDefinition<TSource, TResult>[] filters)
    {
        return new OrFilterDefinition<TSource,TResult>(filters);
    }
        
    public AggregateExpressionDefinition<TSource, TResult> Expression(AggregateExpressionDefinition<TSource, TResult> filter)
    {
        return new ExpressionFilterDefinition<TSource, TResult>(filter);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> In(params AggregateExpressionDefinition<TSource, TResult>[] filters)
    {
        return new InFilterDefinition<TSource,TResult>(filters);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> Not(params AggregateExpressionDefinition<TSource, TResult>[] filters)
    {
        return new NotFilterDefinition<TSource,TResult>(filters);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> SortArray(FieldDefinition<TSource> input, BsonDocument sort)
    {
        return new SortArrayDefinition<TSource, TResult>(input, sort);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> ConcatArrays(params AggregateExpressionDefinition<TSource, TResult>[] arrays)
    {
        return new ConcatArrayDefinition<TSource, TResult>(arrays);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> Reduce(AggregateExpressionDefinition<TSource, TResult> input,
        AggregateExpressionDefinition<TSource, TResult> initialValue,
        AggregateExpressionDefinition<TSource, TResult> @in)
    {
        return new ReduceDefinition<TSource, TResult>(input, initialValue, @in);
    }

    public AggregateExpressionDefinition<TSource, TResult> Field(string field)
    {
        return new FieldAggregateDefinition<TSource, TResult>(field);
    }
    
    public AggregateExpressionDefinition<TSource, TResult> Condition(AggregateExpressionDefinition<TSource, TResult> condition,
        AggregateExpressionDefinition<TSource, TResult> trueDefinition, 
        AggregateExpressionDefinition<TSource, TResult> falseDefinition)
    {
        return new ConditionDefinition<TSource, TResult>(condition, trueDefinition, falseDefinition);
    }

    public AggregateExpressionDefinition<TSource, TResult> Map(AggregateExpressionDefinition<TSource, TResult> input, string @as, AggregateExpressionDefinition<TSource, TResult> @in)
    {
        return new MapDefinition<TSource, TResult>(input, @as, @in);
    }

    public AggregateExpressionDefinition<TSource, TResult> MergeObjects(params AggregateExpressionDefinition<TSource, TResult>[] documents)
    {
        return new MergeObjectsDefinition<TSource, TResult>(documents);
    }
}

internal class ProjectionBuilder<TSource, TResult>
{
     public ProjectionDefinition<TSource, TResult> SingleField(FieldDefinition<TSource> field, AggregateExpressionDefinition<TSource, TResult> value)
     {
          return new SingleFieldProjectionDefinition<TSource, TResult>(field, value);
     }
}