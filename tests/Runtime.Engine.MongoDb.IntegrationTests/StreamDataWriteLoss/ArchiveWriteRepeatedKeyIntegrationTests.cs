using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataWriteLoss;

/// <summary>
///     AB#8679: the affected-row check of an unguarded multi-row upsert, for a row key named twice in one statement,
///     against a real CrateDB. Pins how CrateDB counts such a statement and that the check compares on that basis: no
///     false shortfall for a repeated key, a dropped row is still found next to it. CrateDB (6.4) counts one affected row
///     per row sent, the repeated key included, also for two points less than a millisecond apart that are one stored row
///     (millisecond <c>TIMESTAMP</c>); the check therefore compares with the rows sent, not with the distinct keys, which
///     would miss the dropped row of the last test.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveWriteRepeatedKeyIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId MeterA = new("65f000000000000000000001");
    private static readonly OctoObjectId MeterB = new("65f000000000000000000002");
    private static readonly DateTime Origin = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveColumnDdl[] Columns =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly string[] UserColumns = ["value"];

    [Theory]
    [InlineData(0)] // the same timestamp twice
    [InlineData(3_000)] // 0.3 ms apart: one key in CrateDB
    public async Task CrateDb_CountsARepeatedKeyOfAMultiRowUpsert_OncePerRowSent(long ticksApart)
    {
        var table = await CreateTableAsync();
        try
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString + ";Pooling=false");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {table} (\"timestamp\", \"rtid\", \"cktypeid\", \"value\") VALUES " +
                "($1, $2, $3, 1), ($4, $5, $6, 2), ($7, $8, $9, 3) " +
                "ON CONFLICT (\"timestamp\", \"rtid\", \"cktypeid\") DO UPDATE SET \"value\" = excluded.\"value\"",
                connection);
            foreach (var (at, meter) in new[]
                     {
                         (Origin, MeterA), (Origin.AddTicks(ticksApart), MeterA), (Origin, MeterB)
                     })
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = at });
                cmd.Parameters.Add(new NpgsqlParameter { Value = meter.ToString() });
                cmd.Parameters.Add(new NpgsqlParameter { Value = Type.ToString() });
            }

            var affected = await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            Assert.Equal(3, affected); // one per row sent, the repeated key included
            await fixture.SqlAsync($"REFRESH TABLE {table}");
            var rows = await fixture.QueryAsync($"SELECT \"rtid\", \"value\" FROM {table} ORDER BY \"rtid\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal(2d, Convert.ToDouble(rows[0]["value"], CultureInfo.InvariantCulture)); // the later row won
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task UnguardedWrite_KeyRepeatedLessThanAMillisecondApart_IsNoShortfall()
    {
        var table = await CreateTableAsync();
        try
        {
            var tenant = CrateStagingCopyFixture.NewTenantId();
            await fixture.Client.InsertDataAsync(tenant, table, UserColumns,
            [
                Point(MeterA, Origin, 1), Point(MeterA, Origin.AddTicks(3_000), 2), Point(MeterB, Origin, 3)
            ]);

            await fixture.SqlAsync($"REFRESH TABLE {table}");
            var rows = await fixture.QueryAsync($"SELECT \"rtid\", \"value\" FROM {table} ORDER BY \"rtid\"");
            Assert.Equal([2d, 3d], rows.Select(r => Convert.ToDouble(r["value"], CultureInfo.InvariantCulture)));
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task UnguardedWrite_RowDroppedNextToARepeatedKey_FailsTheWrite()
    {
        var table = await CreateTableAsync(checkNotNegative: true);
        try
        {
            var tenant = CrateStagingCopyFixture.NewTenantId();
            var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
                fixture.Client.InsertDataAsync(tenant, table, UserColumns,
                [
                    Point(MeterA, Origin, 1), Point(MeterA, Origin.AddTicks(3_000), 2), Point(MeterB, Origin, -1)
                ]));

            Assert.Equal(table, ex.QualifiedTable);
            Assert.Equal(1, ex.RowsMissing);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    private static DataPointDto Point(OctoObjectId meter, DateTime at, double value) =>
        new(new Dictionary<string, object?> { ["value"] = value }) { RtId = meter, CkTypeId = Type, Timestamp = at };

    private async Task<string> CreateTableAsync(bool checkNotNegative = false)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString());
        var ddl = ArchiveDdlGenerator.GenerateCreateTable(table, Columns, 2, 0);
        if (checkNotNegative)
        {
            // A multi-row INSERT stores the rows that pass the check and answers without an error (see
            // ArchiveWriteShortfallIntegrationTests): the signature of a dropped shard request.
            Assert.Contains(", PRIMARY KEY (", ddl, StringComparison.Ordinal);
            ddl = ddl.Replace(", PRIMARY KEY (",
                ", CONSTRAINT value_not_negative CHECK (\"value\" >= 0), PRIMARY KEY (", StringComparison.Ordinal);
        }

        await fixture.SqlAsync(ddl);
        return table;
    }

    private Task DropAsync(string table) => fixture.SqlAsync($"DROP TABLE IF EXISTS {table}");
}
