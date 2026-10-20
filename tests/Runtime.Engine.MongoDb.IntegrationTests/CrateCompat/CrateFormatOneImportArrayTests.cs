using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CrateCompat;

/// <summary>
///     AB#7149: the row import (format 1) of an archive with array and record columns. The bot reads the NDJSON export into
///     typed arrays (<c>NdjsonRowReader</c>); CrateDB takes those on 5.x and on 6.4, whereas the JSON text of an array is
///     refused by 5.x for a text or number array and by 6.4 for every array. Run with
///     <c>OCTO_TEST_CRATEDB_IMAGE=crate:5.10.10</c> and with the default <c>crate:6.4.6</c>.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class CrateFormatOneImportArrayTests(CrateStagingCopyFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Start = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [Fact]
    public async Task ImportRows_TypedArraysAndRecords_AreStoredAsExported()
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var archiveRtId = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId.ToString());
        var record = new CrateColumnType.StrictObject(
            [new RecordField("name", new CrateColumnType.Primitive("TEXT")), new RecordField("value", new CrateColumnType.Primitive("DOUBLE PRECISION"))]);
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(table,
        [
            new ArchiveColumnDdl("Tags", new CrateColumnType.Array(new CrateColumnType.Primitive("TEXT")), false, true),
            new ArchiveColumnDdl("Counts", new CrateColumnType.Array(new CrateColumnType.Primitive("INTEGER")), false, true),
            new ArchiveColumnDdl("Ratios", new CrateColumnType.Array(new CrateColumnType.Primitive("DOUBLE PRECISION")), false, true),
            new ArchiveColumnDdl("Records", new CrateColumnType.Array(record), false, true),
            new ArchiveColumnDdl("Record", record, false, true),
        ], 1, 0));

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            // What NdjsonRowReader yields for an exported row.
            Row(1, tags: new[] { "a", "b" }, counts: new long?[] { 1, 2 }, ratios: new double?[] { 1.5, 2 },
                records: new[] { "{\"name\":\"n\",\"value\":1.5}" }, record: "{\"name\":\"n\",\"value\":2.5}"),
            Row(2, tags: new string[] { }, counts: new string[] { }, ratios: new string[] { }, records: new string[] { }, record: null),
            Row(3, tags: new string?[] { "x", null }, counts: new long?[] { 3, null }, ratios: new double?[] { null, 4.5 },
                records: null, record: null),
        };

        await Repository(tenantId, archiveRtId).ImportRowsAsync(archiveRtId, ToAsync(rows), ArchiveImportMode.InsertOnly, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {table}");

        Assert.Equal(3, await fixture.CountAsync($"SELECT COUNT(*) FROM {table}"));
        var first = (await fixture.QueryAsync(
            $"SELECT array_to_string(\"tags\", ',') AS tags, array_to_string(\"counts\", ',') AS counts, " +
            $"array_to_string(\"ratios\", ',') AS ratios, array_length(\"records\", 1) AS records, \"record\"['value'] AS value " +
            $"FROM {table} WHERE \"timestamp\" = {CrateSqlLiteral.Timestamp(Start.AddSeconds(1))}")).Single();
        Assert.Equal("a,b", first["tags"]);
        Assert.Equal("1,2", first["counts"]);
        Assert.Equal("1.5,2.0", first["ratios"]);
        Assert.Equal(1, Convert.ToInt32(first["records"]));
        Assert.Equal(2.5, Convert.ToDouble(first["value"]));
    }

    private static Dictionary<string, object?> Row(
        int second, object? tags, object? counts, object? ratios, object? records, object? record) => new()
    {
        ["rtid"] = OctoObjectId.GenerateNewId().ToString(),
        ["cktypeid"] = "Test/Sensor",
        ["timestamp"] = Start.AddSeconds(second),
        ["tags"] = tags,
        ["counts"] = counts,
        ["ratios"] = ratios,
        ["records"] = records,
        ["record"] = record,
    };

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ToAsync(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        await Task.CompletedTask;
        foreach (var row in rows)
        {
            yield return row;
        }
    }

    private CrateDbStreamDataRepository Repository(string tenantId, OctoObjectId archiveRtId)
    {
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(archiveRtId)).Returns(new ArchiveSnapshot(archiveRtId,
            new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")), CkArchiveStatus.Activated, "raw",
            new[]
            {
                new CkArchiveColumnSpec("Tags", true, false), new CkArchiveColumnSpec("Counts", true, false),
                new CkArchiveColumnSpec("Ratios", true, false), new CkArchiveColumnSpec("Records", true, false),
                new CkArchiveColumnSpec("Record", true, false),
            }));

        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenantId, store, A.Fake<IFormulaEngine>());
    }
}
