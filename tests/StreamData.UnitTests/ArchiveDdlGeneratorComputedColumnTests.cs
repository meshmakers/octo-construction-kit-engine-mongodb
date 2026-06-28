using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class ArchiveDdlGeneratorComputedColumnTests
{
    private static readonly string Table = "\"acmecorp\".\"tempSensor_telemetry\"";

    // Computed columns carry an explicit ColumnName, already normalized to lower-case by the
    // resolver (ColumnNameMapper.PathToColumnName), so tests pass lower-case names verbatim.
    private static ArchiveColumnDdl Computed(string name, string crateType, bool indexed = true) =>
        new(string.Empty, new CrateColumnType.Primitive(crateType), Required: false, Indexed: indexed,
            ColumnName: name);

    // Ingested columns carry a Path; the generator derives the column name from it via
    // ColumnNameMapper (which lower-cases and strips dots).
    private static ArchiveColumnDdl Ingested(string path, string crateType, bool required = false) =>
        new(path, new CrateColumnType.Primitive(crateType), Required: required, Indexed: true);

    [Fact]
    public void ComputedColumn_UsesColumnName_AndIsNullable()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table, new[] { Computed("powerfactor", "DOUBLE PRECISION") }, 3, 1);

        Assert.Contains("\"powerfactor\" DOUBLE PRECISION,", ddl);
        // Computed columns are never NOT NULL — the formula may yield NULL.
        Assert.DoesNotContain("\"powerfactor\" DOUBLE PRECISION NOT NULL", ddl);
    }

    [Fact]
    public void ComputedColumn_IndexedFalse_EmitsIndexOff()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table, new[] { Computed("powerfactor", "DOUBLE PRECISION", indexed: false) }, 3, 1);

        Assert.Contains("\"powerfactor\" DOUBLE PRECISION INDEX OFF,", ddl);
    }

    [Theory]
    [InlineData("BOOLEAN")]
    [InlineData("INTEGER")]
    [InlineData("BIGINT")]
    [InlineData("TIMESTAMP WITH TIME ZONE")]
    public void ComputedColumn_EmitsDeclaredType(string crateType)
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table, new[] { Computed("derived", crateType) }, 3, 1);

        Assert.Contains($"\"derived\" {crateType},", ddl);
    }

    [Fact]
    public void IngestedAndComputed_BothEmitted()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(
            Table,
            new[]
            {
                Ingested("activePower", "DOUBLE PRECISION", required: true),
                Computed("powerfactor", "DOUBLE PRECISION"),
            },
            3, 1);

        // Ingested path is lower-cased by the column-name mapper.
        Assert.Contains("\"activepower\" DOUBLE PRECISION NOT NULL,", ddl);
        Assert.Contains("\"powerfactor\" DOUBLE PRECISION,", ddl);
    }

    [Fact]
    public void ComputedColumn_NameCollidesWithIngested_Throws()
    {
        // Ingested path "powerFactor" lower-cases to "powerfactor", colliding with the computed name.
        var ex = Assert.Throws<ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateTable(
                Table,
                new[]
                {
                    Ingested("powerFactor", "DOUBLE PRECISION"),
                    Computed("powerfactor", "DOUBLE PRECISION"),
                },
                3, 1));

        Assert.Contains("powerfactor", ex.Message);
    }

    [Fact]
    public void ComputedColumn_WorksOnWindowedTable()
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            Table, new[] { Computed("powerfactor", "DOUBLE PRECISION") }, 3, 1);

        Assert.Contains("\"powerfactor\" DOUBLE PRECISION,", ddl);
        Assert.Contains("\"window_start\"", ddl);
    }

    [Fact]
    public void GenerateAddColumn_ComputedColumn_EmitsAlterAddColumn()
    {
        var sql = ArchiveDdlGenerator.GenerateAddColumn(Table, Computed("powerfactor", "DOUBLE PRECISION"));

        Assert.Equal(
            "ALTER TABLE \"acmecorp\".\"tempSensor_telemetry\" ADD COLUMN \"powerfactor\" DOUBLE PRECISION;",
            sql);
    }

    [Fact]
    public void GenerateAddColumn_IndexedFalse_EmitsIndexOff()
    {
        var sql = ArchiveDdlGenerator.GenerateAddColumn(
            Table, Computed("powerfactor", "DOUBLE PRECISION", indexed: false));

        Assert.Equal(
            "ALTER TABLE \"acmecorp\".\"tempSensor_telemetry\" ADD COLUMN \"powerfactor\" DOUBLE PRECISION INDEX OFF;",
            sql);
    }

    [Fact]
    public void GenerateAddColumn_EmptyTable_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateAddColumn(" ", Computed("powerfactor", "DOUBLE PRECISION")));
    }
}
