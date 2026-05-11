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
