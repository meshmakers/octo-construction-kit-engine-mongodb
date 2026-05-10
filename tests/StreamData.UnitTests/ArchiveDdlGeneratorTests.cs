using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class ArchiveDdlGeneratorTests
{
    private static readonly string Table = "\"acmecorp\".\"tempSensor_telemetry\"";

    private const string ExpectedStandardPrefix =
        "CREATE TABLE IF NOT EXISTS \"acmecorp\".\"tempSensor_telemetry\" ( " +
        "\"rtid\" TEXT NOT NULL, " +
        "\"timestamp\" TIMESTAMP WITH TIME ZONE NOT NULL, " +
        "\"cktypeid\" TEXT NOT NULL, " +
        "\"rtcreationdatetime\" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP, " +
        "\"rtchangeddatetime\" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP, " +
        "\"rtwellknownname\" TEXT,";

    private const string ExpectedTrailingPk = " PRIMARY KEY (\"timestamp\", \"rtid\", \"cktypeid\")";

    [Fact]
    public void NoArchiveColumns_EmitsStandardColumnsAndPk()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(Table, Array.Empty<ArchiveColumnDdl>(), 3, 1);

        Assert.Equal(
            ExpectedStandardPrefix + ExpectedTrailingPk +
            ") CLUSTERED INTO 3 SHARDS WITH (number_of_replicas = 1);",
            ddl);
    }

    [Fact]
    public void NegativeReplicas_OmitsReplicaClause()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(Table, Array.Empty<ArchiveColumnDdl>(), 3, -1);

        Assert.EndsWith(") CLUSTERED INTO 3 SHARDS;", ddl);
        Assert.DoesNotContain("number_of_replicas", ddl);
    }

    [Fact]
    public void ScalarRequired_EmitsNotNull()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("voltage", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: true, Indexed: true),
            },
            3, 1);

        Assert.Contains("\"voltage\" DOUBLE PRECISION NOT NULL,", ddl);
        Assert.DoesNotContain("INDEX OFF", ddl);
    }

    [Fact]
    public void ScalarOptional_NoNotNull()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("current", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: true),
            },
            3, 1);

        Assert.Contains("\"current\" DOUBLE PRECISION,", ddl);
        Assert.DoesNotContain("\"current\" DOUBLE PRECISION NOT NULL", ddl);
    }

    [Fact]
    public void IndexedFalse_EmitsIndexOff()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("errorText", new CrateColumnType.Primitive("TEXT"), Required: false, Indexed: false),
            },
            3, 1);

        Assert.Contains("\"errortext\" TEXT INDEX OFF,", ddl);
    }

    [Fact]
    public void RequiredAndNotIndexed_BothFlagsEmitted()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("note", new CrateColumnType.Primitive("TEXT"), Required: true, Indexed: false),
            },
            3, 1);

        Assert.Contains("\"note\" TEXT NOT NULL INDEX OFF,", ddl);
    }

    [Fact]
    public void NestedScalarPath_KeptAsQuotedColumnName()
    {
        // Multi-segment paths get concatenated and lower-cased: `sensor.reading.value` →
        // `sensorreadingvalue`. Lower-case storage avoids CrateDB's case-preservation quirks
        // for quoted mixed-case identifiers in some contexts.
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl(
                    "sensor.reading.value",
                    new CrateColumnType.Primitive("DOUBLE PRECISION"),
                    Required: true,
                    Indexed: true),
            },
            3, 1);

        Assert.Contains("\"sensorreadingvalue\" DOUBLE PRECISION NOT NULL,", ddl);
    }

    [Fact]
    public void RecordPath_EmitsObjectStrictWithSubfields()
    {
        var record = new CrateColumnType.StrictObject(new[]
        {
            new RecordField("value", new CrateColumnType.Primitive("DOUBLE PRECISION")),
            new RecordField("unit", new CrateColumnType.Primitive("TEXT")),
        });

        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("reading", record, Required: false, Indexed: true),
            },
            3, 1);

        Assert.Contains(
            "\"reading\" OBJECT(STRICT) AS (\"value\" DOUBLE PRECISION, \"unit\" TEXT),",
            ddl);
    }

    [Fact]
    public void ScalarArrayPath_EmitsArray()
    {
        // The path picker strips bracket syntax before reaching the DDL generator; this test now
        // checks the typical case of a flat array path being emitted as ARRAY(<primitive>).
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl(
                    "readings",
                    new CrateColumnType.Array(new CrateColumnType.Primitive("DOUBLE PRECISION")),
                    Required: true,
                    Indexed: true),
            },
            3, 1);

        // Array column: NOT NULL applies to the array itself; element gaps are still allowed.
        Assert.Contains("\"readings\" ARRAY(DOUBLE PRECISION) NOT NULL,", ddl);
    }

    [Fact]
    public void RecordArrayPath_EmitsArrayOfObjectStrict()
    {
        var element = new CrateColumnType.StrictObject(new[]
        {
            new RecordField("value", new CrateColumnType.Primitive("DOUBLE PRECISION")),
            new RecordField("ts", new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE")),
        });

        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("readings", new CrateColumnType.Array(element), Required: false, Indexed: true),
            },
            3, 1);

        Assert.Contains(
            "\"readings\" ARRAY(OBJECT(STRICT) AS (\"value\" DOUBLE PRECISION, \"ts\" TIMESTAMP WITH TIME ZONE)),",
            ddl);
    }

    [Fact]
    public void MultipleColumnsMixed_EmittedInOrder()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                new ArchiveColumnDdl("voltage", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: true, Indexed: true),
                new ArchiveColumnDdl("current", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: true),
                new ArchiveColumnDdl("note", new CrateColumnType.Primitive("TEXT"), Required: false, Indexed: false),
            },
            3, 1);

        var voltagePos = ddl.IndexOf("\"voltage\"", StringComparison.Ordinal);
        var currentPos = ddl.IndexOf("\"current\"", StringComparison.Ordinal);
        var notePos = ddl.IndexOf("\"note\"", StringComparison.Ordinal);

        Assert.True(voltagePos > 0 && currentPos > voltagePos && notePos > currentPos,
            "Columns must appear in declaration order, after the standard columns.");
    }

    [Fact]
    public void DuplicatePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateTable(
                Table,
                new[]
                {
                    new ArchiveColumnDdl("voltage", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: true, Indexed: true),
                    new ArchiveColumnDdl("voltage", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: true),
                },
                3, 1));
    }

    [Fact]
    public void PathCollidesWithStandardColumn_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateTable(
                Table,
                new[]
                {
                    new ArchiveColumnDdl("rtid", new CrateColumnType.Primitive("TEXT"), Required: true, Indexed: true),
                },
                3, 1));
    }

    [Fact]
    public void EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateTable(
                Table,
                new[]
                {
                    new ArchiveColumnDdl("", new CrateColumnType.Primitive("TEXT"), Required: true, Indexed: true),
                },
                3, 1));
    }

    [Fact]
    public void EmptyTableName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateTable("", Array.Empty<ArchiveColumnDdl>(), 3, 1));
    }

    [Fact]
    public void NumberOfShardsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArchiveDdlGenerator.GenerateCreateTable(Table, Array.Empty<ArchiveColumnDdl>(), 0, 1));
    }

    [Fact]
    public void GenerateDropTable_FormatsAsDropTableIfExists()
    {
        Assert.Equal(
            "DROP TABLE IF EXISTS \"acmecorp\".\"tempSensor_telemetry\";",
            ArchiveDdlGenerator.GenerateDropTable(Table));
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.Boolean, "BOOLEAN")]
    [InlineData(AttributeValueTypesDto.Integer, "INTEGER")]
    [InlineData(AttributeValueTypesDto.Integer64, "BIGINT")]
    [InlineData(AttributeValueTypesDto.Double, "DOUBLE PRECISION")]
    [InlineData(AttributeValueTypesDto.String, "TEXT")]
    [InlineData(AttributeValueTypesDto.DateTime, "TIMESTAMP WITH TIME ZONE")]
    [InlineData(AttributeValueTypesDto.DateTimeOffset, "TIMESTAMP WITH TIME ZONE")]
    [InlineData(AttributeValueTypesDto.Enum, "INTEGER")]
    [InlineData(AttributeValueTypesDto.GeospatialPoint, "GEO_POINT")]
    [InlineData(AttributeValueTypesDto.TimeSpan, "BIGINT")]
    public void CrateTypeMapper_MapsKnownPrimitiveTypes(AttributeValueTypesDto src, string expected)
    {
        Assert.Equal(expected, CrateTypeMapper.ToCratePrimitive(src).CrateTypeName);
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.Binary)]
    [InlineData(AttributeValueTypesDto.BinaryLinked)]
    [InlineData(AttributeValueTypesDto.Record)]
    [InlineData(AttributeValueTypesDto.RecordArray)]
    [InlineData(AttributeValueTypesDto.StringArray)]
    [InlineData(AttributeValueTypesDto.IntegerArray)]
    public void CrateTypeMapper_NonPrimitiveTypes_Throw(AttributeValueTypesDto src)
    {
        Assert.Throws<ArgumentException>(() => CrateTypeMapper.ToCratePrimitive(src));
    }
}
