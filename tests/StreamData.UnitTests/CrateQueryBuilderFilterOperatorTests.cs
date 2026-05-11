using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

public class CrateQueryBuilderFilterOperatorTests
{
    [Fact]
    public void Between_EmitsSqlWithBothValues()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Timestamp", StreamDataFieldFilterOperator.Between,
            "2026-01-01 00:00:00.000Z", isDataField: false,
            secondaryValue: "2026-01-02 00:00:00.000Z");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"Timestamp\" BETWEEN '2026-01-01 00:00:00.000Z' AND '2026-01-02 00:00:00.000Z'", query);
    }

    [Fact]
    public void In_EmitsSqlWithCommaSeparatedValues()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("RtId", StreamDataFieldFilterOperator.In, "",
            isDataField: false,
            valueList: ["id1", "id2", "id3"]);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"RtId\" IN ('id1', 'id2', 'id3')", query);
    }

    [Fact]
    public void NotIn_EmitsSqlWithNotInKeyword()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("RtId", StreamDataFieldFilterOperator.NotIn, "",
            isDataField: false,
            valueList: ["id1", "id2"]);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"RtId\" NOT IN ('id1', 'id2')", query);
    }

    [Fact]
    public void IsNull_EmitsIsNullCheck()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.IsNull, "",
            isDataField: true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"data['Voltage']\" IS NULL", query);
    }

    [Fact]
    public void IsNotNull_EmitsIsNotNullCheck()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.IsNotNull, "",
            isDataField: true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"data['Voltage']\" IS NOT NULL", query);
    }
}
