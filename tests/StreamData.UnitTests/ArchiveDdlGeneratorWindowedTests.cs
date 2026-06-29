using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

// Pins the time-range archive DDL shape introduced in concept-time-range-archives §4. The natural
// key (window_start, window_end, rtid, ckTypeId) and the was_updated flag column are the load-
// bearing parts; if either changes accidentally, every existing TimeRangeArchive table breaks on
// the next activation.
public class ArchiveDdlGeneratorWindowedTests
{
    [Fact]
    public void GenerateCreateTimeRangeTable_EmitsTwoTimestampColumns_AndCompositePrimaryKey()
    {
        var sql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            qualifiedTableName: "\"loxone\".\"archive_eda\"",
            columns: new[]
            {
                new ArchiveColumnDdl("energyConsumed", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: true),
            },
            numberOfShards: 4,
            numberOfReplicas: 1);

        Assert.Contains("\"window_start\" TIMESTAMP WITH TIME ZONE NOT NULL", sql);
        Assert.Contains("\"window_end\" TIMESTAMP WITH TIME ZONE NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\")", sql);
        Assert.DoesNotContain("\"timestamp\"", sql);
        Assert.Contains("CLUSTERED INTO 4 SHARDS", sql);
        Assert.Contains("number_of_replicas = 1", sql);
    }

    [Fact]
    public void GenerateCreateTimeRangeTable_EmitsWasUpdatedFlag_DefaultFalse()
    {
        var sql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            qualifiedTableName: "\"loxone\".\"archive_eda\"",
            columns: System.Array.Empty<ArchiveColumnDdl>(),
            numberOfShards: 1,
            numberOfReplicas: -1);

        Assert.Contains("\"was_updated\" BOOLEAN NOT NULL DEFAULT FALSE", sql);
    }

    [Fact]
    public void GenerateCreateTimeRangeTable_EmitsUserColumnsAfterStandardColumns()
    {
        var sql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            qualifiedTableName: "\"loxone\".\"archive_eda\"",
            columns: new[]
            {
                new ArchiveColumnDdl("energyConsumed", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: true),
                new ArchiveColumnDdl("tariff", new CrateColumnType.Primitive("TEXT"), Required: false, Indexed: false),
            },
            numberOfShards: 1,
            numberOfReplicas: -1);

        // User columns are emitted with their resolved CrateDB types and the INDEX OFF marker when
        // not indexed. The path-to-column mapping (camelCase → lower-case via ColumnNameMapper)
        // applies to both raw and time-range tables.
        Assert.Contains("\"energyconsumed\" DOUBLE PRECISION", sql);
        Assert.Contains("\"tariff\" TEXT INDEX OFF", sql);
    }

    [Fact]
    public void GenerateCreateWindowedTable_DefaultOmitsGenerationColumn_TimeRangeShapeUnchanged()
    {
        // Time-range archives pass includeGeneration:false (the default) and must keep the pre-Phase-6
        // shape — no generation column, generation absent from the PK.
        var sql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            qualifiedTableName: "\"loxone\".\"archive_eda\"",
            columns: System.Array.Empty<ArchiveColumnDdl>(),
            numberOfShards: 1,
            numberOfReplicas: -1);

        Assert.DoesNotContain("\"generation\"", sql);
        Assert.Contains("PRIMARY KEY (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\")", sql);
    }

    [Fact]
    public void GenerateCreateWindowedTable_IncludeGeneration_AddsColumnAndExtendsPrimaryKey()
    {
        // Rollup archives pass includeGeneration:true (AB#4184, Phase 6): a generation column keyed
        // into the PK so a recompute's new generation coexists with the previous one until the flip.
        var sql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            qualifiedTableName: "\"acmecorp\".\"archive_rollup1\"",
            columns: new[]
            {
                new ArchiveColumnDdl("voltage_avg_sum", new CrateColumnType.Primitive("DOUBLE PRECISION"), Required: false, Indexed: true),
            },
            numberOfShards: 1,
            numberOfReplicas: -1,
            includeGeneration: true);

        Assert.Contains("\"generation\" BIGINT NOT NULL DEFAULT 0", sql);
        Assert.Contains("PRIMARY KEY (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"generation\")", sql);
    }

    [Fact]
    public void GenerateCreateTimeRangeTable_RejectsEmptyQualifiedTable()
    {
        Assert.Throws<System.ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateWindowedTable(
                qualifiedTableName: "",
                columns: System.Array.Empty<ArchiveColumnDdl>(),
                numberOfShards: 1,
                numberOfReplicas: -1));
    }

    [Fact]
    public void GenerateCreateTimeRangeTable_RejectsZeroShards()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ArchiveDdlGenerator.GenerateCreateWindowedTable(
                qualifiedTableName: "\"loxone\".\"archive_eda\"",
                columns: System.Array.Empty<ArchiveColumnDdl>(),
                numberOfShards: 0,
                numberOfReplicas: -1));
    }

    [Fact]
    public void GenerateCreateTimeRangeTable_DoubleColumnName_Throws()
    {
        // Two paths that collide on the camelCase mapping (here: same path twice) must surface
        // as a hard error — silently overwriting one is a footgun.
        Assert.Throws<System.ArgumentException>(() =>
            ArchiveDdlGenerator.GenerateCreateWindowedTable(
                qualifiedTableName: "\"loxone\".\"archive_eda\"",
                columns: new[]
                {
                    new ArchiveColumnDdl("Temperature", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true),
                    new ArchiveColumnDdl("Temperature", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true),
                },
                numberOfShards: 1,
                numberOfReplicas: -1));
    }
}
