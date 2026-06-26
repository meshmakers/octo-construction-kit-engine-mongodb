using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// After T17 every column is a first-class identifier on the per-archive table — these tests
// assert the field-filter SQL uses the camelCase column name directly with no `data[...]`
// indirection. The query builder also takes a fully qualified table identifier (schema.table)
// rather than a raw tenant id.
public class CrateQueryBuilderFilterOperatorTests
{
    private const string Table = "\"meshtest\".\"archive_a1\"";

    [Fact]
    public void Between_EmitsSqlWithBothValues()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);
        queryBuilder.AddFieldFilter("timestamp", StreamDataFieldFilterOperator.Between,
            "2026-01-01 00:00:00.000Z",
            secondaryValue: "2026-01-02 00:00:00.000Z");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"timestamp\" BETWEEN '2026-01-01 00:00:00.000Z' AND '2026-01-02 00:00:00.000Z'", query);
    }

    [Fact]
    public void In_EmitsSqlWithCommaSeparatedValues()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("rtid", StreamDataFieldFilterOperator.In, "",
            valueList: ["id1", "id2", "id3"]);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"rtid\" IN ('id1', 'id2', 'id3')", query);
    }

    [Fact]
    public void NotIn_EmitsSqlWithNotInKeyword()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("rtid", StreamDataFieldFilterOperator.NotIn, "",
            valueList: ["id1", "id2"]);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"rtid\" NOT IN ('id1', 'id2')", query);
    }

    [Fact]
    public void In_ParsesBracketWrappedStringValue_IntoIndividualValues()
    {
        // The GraphQL/persisted comparisonValue for an IN filter arrives as a single string in
        // array form (same syntax the Mongo runtime path accepts). It must be unwrapped into
        // separate IN values rather than treated as one literal.
        var valueList = StreamDataFieldFilterValueParser.ParseInValues("[id1, id2, id3]");

        Assert.Equal(["id1", "id2", "id3"], valueList);
    }

    [Fact]
    public void In_ParsesMultiWrappedAndQuotedStringValue()
    {
        // Real-world value observed from the Studio query editor: multi-bracketed, comma+space
        // separated. Trim('[',']') collapses the extra brackets; quotes and whitespace are stripped.
        var valueList = StreamDataFieldFilterValueParser.ParseInValues(
            "[[[[6a0ee04a425c29914c86a54a, \"6a0ee049425c29914c86a4f1\"]]]]");

        Assert.Equal(["6a0ee04a425c29914c86a54a", "6a0ee049425c29914c86a4f1"], valueList);
    }

    [Fact]
    public void In_BracketWrappedValue_EmitsCommaSeparatedSql()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("rtid", StreamDataFieldFilterOperator.In, "",
            valueList: StreamDataFieldFilterValueParser.ParseInValues(
                "[6a0ee04a425c29914c86a54a, 6a0ee049425c29914c86a4f1]"));

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"rtid\" IN ('6a0ee04a425c29914c86a54a', '6a0ee049425c29914c86a4f1')", query);
    }

    [Fact]
    public void In_PlainScalarString_IsSingleValue()
    {
        var valueList = StreamDataFieldFilterValueParser.ParseInValues("id1");

        Assert.Equal(["id1"], valueList);
    }

    [Fact]
    public void In_EnumerableValue_IsUsedAsIs()
    {
        var valueList = StreamDataFieldFilterValueParser.ParseInValues(new[] { "id1", "id2" });

        Assert.Equal(["id1", "id2"], valueList);
    }

    [Fact]
    public void IsNull_EmitsIsNullCheck()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);
        queryBuilder.AddFieldFilter("voltage", StreamDataFieldFilterOperator.IsNull, "");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"voltage\" IS NULL", query);
    }

    [Fact]
    public void IsNotNull_EmitsIsNotNullCheck()
    {
        var queryBuilder = new CrateQueryBuilder(Table);
        queryBuilder.AddVariable("voltage", null, null);
        queryBuilder.AddFieldFilter("voltage", StreamDataFieldFilterOperator.IsNotNull, "");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"voltage\" IS NOT NULL", query);
    }
}
