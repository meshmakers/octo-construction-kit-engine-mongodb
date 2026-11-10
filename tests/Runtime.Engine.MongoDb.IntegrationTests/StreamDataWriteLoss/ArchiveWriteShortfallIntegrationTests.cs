using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataWriteLoss;

/// <summary>
///     AB#8679 without memory pressure: rows CrateDB drops from a multi-row write without an error fail the write, on the
///     guarded and the unguarded path, against a real CrateDB, in a second.
/// </summary>
/// <remarks>
///     The loss is produced on purpose: the archive table carries a <c>CHECK ("value" &gt;= 0)</c> constraint, and a multi-row
///     <c>INSERT</c> stores the rows that pass it and answers the statement without an error, exactly like a shard request a
///     tripped memory breaker ended (a guarded statement lacks the <c>RETURNING</c> rows of the dropped rows, an unguarded one
///     reports fewer affected rows). The rows fail the retry the same way, so the write must end in
///     <see cref="StreamDataWriteIncompleteException" />; the valid rows are stored. The retry that succeeds is covered by
///     <c>MissingRowRetryTests</c>; the same check under real memory pressure by the <c>Category=Stress</c> class
///     <see cref="ArchiveWriteUnderMemoryPressureIntegrationTests" />.
/// </remarks>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveWriteShortfallIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const int Rows = 6;
    private static readonly int[] Dropped = [1, 4];

    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId Meter = new("65f000000000000000000001");
    private static readonly DateTime Origin = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveConflictKey[] Precedence =
    [
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
        new("dataquality", ConflictKeyOrder.LowerWins)
    ];

    private static readonly ArchiveColumnDdl[] Columns =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("DataQuality", new CrateColumnType.Primitive("INTEGER"), false, false)
    ];

    private static readonly string[] UserColumns = ["value", "dataquality"];

    /// <summary>The value of row <paramref name="i" />; the rows in <see cref="Dropped" /> fail the table's check.</summary>
    private static double Value(int i) => Dropped.Contains(i) ? -1d : i;

    private static Dictionary<string, object?> Attributes(int i) => new() { ["value"] = Value(i), ["dataquality"] = 2 };

    [Fact]
    public async Task GuardedWrite_RowsCrateDbDropsWithoutAnError_FailTheWrite_AfterOneRetryOfThoseRows()
    {
        var (tenant, table) = await CreateTableAsync(windowed: true);
        try
        {
            var points = Enumerable.Range(0, Rows).Select(i => new TimeRangeDataPointDto(Attributes(i))
            {
                RtId = Meter, CkTypeId = Type, From = Origin.AddMinutes(15 * i), To = Origin.AddMinutes(15 * (i + 1))
            }).ToList();

            var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
                fixture.Client.InsertTimeRangeDataAsync(tenant, table, UserColumns, points, conflictPrecedence: Precedence));

            Assert.Equal(table, ex.QualifiedTable);
            Assert.Equal(Dropped.Length, ex.RowsSent); // only the missing rows are written once more
            Assert.Equal(Dropped.Length, ex.RowsMissing);
            Assert.Contains("had no RETURNING row", ex.Message, StringComparison.Ordinal);
            await AssertTheValidRowsAreStoredAsync(table);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    [Fact]
    public async Task UnguardedWrite_AnAffectedRowShortfall_FailsTheWrite_AfterOneRetryOfTheStatement()
    {
        var (tenant, table) = await CreateTableAsync(windowed: false);
        try
        {
            var points = Enumerable.Range(0, Rows).Select(i => new DataPointDto(Attributes(i))
            {
                RtId = Meter, CkTypeId = Type, Timestamp = Origin.AddMinutes(15 * i)
            }).ToList();

            var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
                fixture.Client.InsertDataAsync(tenant, table, UserColumns, points));

            Assert.Equal(table, ex.QualifiedTable);
            Assert.Equal(Rows, ex.RowsSent); // the count does not name the rows: the whole statement is repeated
            Assert.Equal(Dropped.Length, ex.RowsMissing);
            Assert.Contains($"CrateDB reported {Rows - Dropped.Length} affected rows for {Rows} rows sent", ex.Message,
                StringComparison.Ordinal);
            await AssertTheValidRowsAreStoredAsync(table);
        }
        finally
        {
            await DropAsync(table);
        }
    }

    private async Task<(string Tenant, string Table)> CreateTableAsync(bool windowed)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString());
        var ddl = windowed
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(table, Columns, 2, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(table, Columns, 2, 0);
        // The DDL lists its columns with a trailing comma before the key; the check goes in front of the key.
        Assert.Contains(", PRIMARY KEY (", ddl, StringComparison.Ordinal);
        await fixture.SqlAsync(ddl.Replace(", PRIMARY KEY (",
            ", CONSTRAINT value_not_negative CHECK (\"value\" >= 0), PRIMARY KEY (", StringComparison.Ordinal));
        return (tenant, table);
    }

    private async Task AssertTheValidRowsAreStoredAsync(string table)
    {
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var values = (await fixture.QueryAsync($"SELECT \"value\" FROM {table} ORDER BY \"value\""))
            .Select(r => Convert.ToDouble(r["value"], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(Enumerable.Range(0, Rows).Where(i => !Dropped.Contains(i)).Select(i => (double)i), values);
    }

    private Task DropAsync(string table) => fixture.SqlAsync($"DROP TABLE IF EXISTS {table}");
}
