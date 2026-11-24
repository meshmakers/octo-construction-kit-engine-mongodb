using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataWriteLoss;

/// <summary>
///     AB#8875 AC3: a guarded time-range write of about 130,000 rows stores the same result with its sub-batches running
///     four at a time as one after another: same rows, values and origins, same skipped rows with the same reasons
///     ($Origin, DataQuality and SourceDocumentDate guards, a key written twice in one batch with the same ranks, where the
///     later write wins), and the same failure when the missing-row retry stays short. Each mode writes its own table of
///     the same CrateDB.
/// </summary>
/// <remarks>
///     The concurrency itself (limit reached, never exceeded, no key in two statements at once) is proven by
///     <c>InsertStatementLanesTests</c>; the retry that succeeds by <c>MissingRowRetryTests</c>.
/// </remarks>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveWriteConcurrencyIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const int Meters = 130;
    private const int Windows = 1_000;
    private const int Rows = Meters * Windows;
    private const int RepeatedRows = 2_000;
    private const int NearRows = 8_000;

    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Origin = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DocumentDate = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveConflictKey[] Precedence =
    [
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
        new("dataquality", ConflictKeyOrder.LowerWins),
        new("sourcedocumentdate", ConflictKeyOrder.HigherWins)
    ];

    private static readonly ArchiveColumnDdl[] Columns =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("DataQuality", new CrateColumnType.Primitive("INTEGER"), false, false),
        new("SourceDocumentDate", new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE"), false, false)
    ];

    private static readonly string[] UserColumns = ["value", "dataquality", "sourcedocumentdate"];

    private static readonly string[] ComparedColumns =
    [
        "window_start", "window_end", "rtid", "cktypeid", "value", "dataquality", "sourcedocumentdate",
        "row_origin", "manual_written_by", "manual_reason", "was_updated"
    ];

    private static OctoObjectId Meter(int m) => new($"65f0000000000000000{m:D5}");

    private static TimeRangeDataPointDto Point(int row, double value, int quality, DateTime documentDate,
        ArchiveRowOrigin origin = ArchiveRowOrigin.Automated)
    {
        var window = row / Meters;
        return new TimeRangeDataPointDto(new Dictionary<string, object?>
        {
            ["value"] = value, ["dataquality"] = quality, ["sourcedocumentdate"] = documentDate
        })
        {
            RtId = Meter(row % Meters), CkTypeId = Type,
            From = Origin.AddMinutes(15 * window), To = Origin.AddMinutes(15 * (window + 1)),
            Origin = origin,
            ManualWrittenBy = origin == ArchiveRowOrigin.Manual ? "jane.doe" : null,
            ManualWrittenAt = origin == ArchiveRowOrigin.Manual ? DocumentDate : null,
            ManualReason = origin == ArchiveRowOrigin.Manual ? "meter replaced" : null
        };
    }

    /// <summary>Every row once, automated with quality 2; every 50th row manual.</summary>
    private static List<TimeRangeDataPointDto> FirstDelivery() => Enumerable.Range(0, Rows)
        .Select(i => Point(i, i, 2, DocumentDate, i % 50 == 0 ? ArchiveRowOrigin.Manual : ArchiveRowOrigin.Automated))
        .ToList();

    /// <summary>
    ///     Every row again: a worse quality, a better quality, the same quality from an older document and the same quality
    ///     from a newer document, one quarter each; then 2,000 rows that write a key of this batch a second time.
    /// </summary>
    private static List<TimeRangeDataPointDto> SecondDelivery()
    {
        var points = Enumerable.Range(0, Rows).Select(i => Second(i, 1_000_000 + i)).ToList();
        points.AddRange(Enumerable.Range(0, RepeatedRows).Select(j => Second(j * 61 % Rows, 2_000_000 + j)));
        return points;
    }

    private static TimeRangeDataPointDto Second(int row, double value) => (row % 4) switch
    {
        0 => Point(row, value, 3, DocumentDate),
        1 => Point(row, value, 1, DocumentDate),
        2 => Point(row, value, 2, DocumentDate.AddHours(-1)),
        _ => Point(row, value, 2, DocumentDate.AddHours(1))
    };

    /// <summary>
    ///     Rows 0 to 7,999 again as in <see cref="SecondDelivery" />; from row 4,000 on, every fourth row is followed by a
    ///     key of this batch written once more, 1,000 to 3,000 rows earlier: with the same ranks as its first write in the
    ///     batch, a worse quality or a better quality. One statement after another puts the two writes of such a key into
    ///     two statements, four lanes put most of them into one.
    /// </summary>
    private static List<TimeRangeDataPointDto> NearRepeatDelivery()
    {
        var points = new List<TimeRangeDataPointDto>();
        for (var i = 0; i < NearRows; i++)
        {
            points.Add(Second(i, 1_000_000 + i));
            if (i >= 4_000 && i % 4 == 0)
            {
                var row = i - 1_000 - i * 7 % 2_001;
                points.Add((i / 4 % 3) switch
                {
                    0 => Second(row, 3_000_000 + i),
                    1 => Point(row, 3_000_000 + i, 3, DocumentDate.AddHours(2)),
                    _ => Point(row, 3_000_000 + i, 1, DocumentDate.AddHours(-2))
                });
            }
        }

        return points;
    }

    [Fact]
    public async Task AGuardedWriteOf130000Rows_StoresAndSkipsTheSameRows_FourStatementsAtATimeAsOneAfterAnother()
    {
        var sequential = NewTable();
        var concurrent = NewTable();
        await using var sequentialClient = CreateClient(1);
        await using var concurrentClient = CreateClient(4);
        try
        {
            await CreateTableAsync(sequential.Table, withCheck: false);
            await CreateTableAsync(concurrent.Table, withCheck: false);
            var firstSequential = await sequentialClient.Client.InsertTimeRangeDataAsync(sequential.Tenant, sequential.Table,
                UserColumns, FirstDelivery(), conflictPrecedence: Precedence);
            var firstConcurrent = await concurrentClient.Client.InsertTimeRangeDataAsync(concurrent.Tenant, concurrent.Table,
                UserColumns, FirstDelivery(), conflictPrecedence: Precedence);
            Assert.Empty(firstSequential);
            Assert.Empty(firstConcurrent);

            var skippedSequential = await sequentialClient.Client.InsertTimeRangeDataAsync(sequential.Tenant, sequential.Table,
                UserColumns, SecondDelivery(), conflictPrecedence: Precedence);
            var skippedConcurrent = await concurrentClient.Client.InsertTimeRangeDataAsync(concurrent.Tenant, concurrent.Table,
                UserColumns, SecondDelivery(), conflictPrecedence: Precedence);

            Assert.Contains(skippedSequential, s => s.Reason == StreamDataSkipReason.LowerPrecedence);
            Assert.Contains(skippedSequential, s => s.Reason == StreamDataSkipReason.ManualValueProtected);
            Assert.Equal(skippedSequential, skippedConcurrent);

            var storedSequential = await ReadAsync(sequential.Table);
            var storedConcurrent = await ReadAsync(concurrent.Table);
            Assert.Equal(Rows, storedSequential.Count);
            Assert.Equal(storedSequential, storedConcurrent);
            Assert.Contains(storedSequential, r => r.Contains("|2000001|", StringComparison.Ordinal));
        }
        finally
        {
            await DropAsync(sequential.Table);
            await DropAsync(concurrent.Table);
        }
    }

    [Fact]
    public async Task AKeyWrittenAgain1000To3000RowsLater_StoresAndSkipsTheSameRows_FourStatementsAtATimeAsOneAfterAnother()
    {
        var keys = NearRepeatDelivery().Select(p => CrateDatabaseClient.RowKey(p.RtId, p.CkTypeId, p.From, p.To)).ToArray();
        Assert.False(SharesAStatement(keys, 1));
        Assert.True(SharesAStatement(keys, 4));

        var sequential = NewTable();
        var concurrent = NewTable();
        await using var sequentialClient = CreateClient(1);
        await using var concurrentClient = CreateClient(4);
        try
        {
            await CreateTableAsync(sequential.Table, withCheck: false);
            await CreateTableAsync(concurrent.Table, withCheck: false);
            Assert.Empty(await sequentialClient.Client.InsertTimeRangeDataAsync(sequential.Tenant, sequential.Table,
                UserColumns, FirstDelivery().Take(NearRows), conflictPrecedence: Precedence));
            Assert.Empty(await concurrentClient.Client.InsertTimeRangeDataAsync(concurrent.Tenant, concurrent.Table,
                UserColumns, FirstDelivery().Take(NearRows), conflictPrecedence: Precedence));

            var skippedSequential = await sequentialClient.Client.InsertTimeRangeDataAsync(sequential.Tenant, sequential.Table,
                UserColumns, NearRepeatDelivery(), conflictPrecedence: Precedence);
            var skippedConcurrent = await concurrentClient.Client.InsertTimeRangeDataAsync(concurrent.Tenant, concurrent.Table,
                UserColumns, NearRepeatDelivery(), conflictPrecedence: Precedence);

            Assert.Contains(skippedSequential, s => s.Index >= 4_000 && s.Reason == StreamDataSkipReason.LowerPrecedence);
            Assert.Contains(skippedSequential, s => s.Index >= 4_000 && s.Reason == StreamDataSkipReason.ManualValueProtected);
            Assert.Equal(skippedSequential, skippedConcurrent);

            var storedSequential = await ReadAsync(sequential.Table);
            var storedConcurrent = await ReadAsync(concurrent.Table);
            Assert.Equal(NearRows, storedSequential.Count);
            Assert.Equal(storedSequential, storedConcurrent);
            Assert.Contains(storedSequential, r => r.Split('|')[4].StartsWith("300", StringComparison.Ordinal));
        }
        finally
        {
            await DropAsync(sequential.Table);
            await DropAsync(concurrent.Table);
        }
    }

    /// <summary>Whether a statement of the plan the client makes with this limit carries a key twice.</summary>
    private static bool SharesAStatement(string[] keys, int limit)
    {
        var rowsPerStatement = CrateDatabaseClient.RowsPerStatement(5 + ArchiveRowOriginColumns.Names.Count + UserColumns.Length);
        return CrateDatabaseClient.PlanInsertStatements(keys, Enumerable.Range(0, keys.Length).ToList(), k => k,
                rowsPerStatement, limit)
            .SelectMany(lane => lane)
            .Any(statement => statement.Select(r => keys[r]).Distinct(StringComparer.Ordinal).Count() < statement.Length);
    }

    [Fact]
    public async Task AGuardedWriteWhoseRetryStaysShort_FailsWithTheSameTableAndCounts_FourStatementsAtATimeAsOneAfterAnother()
    {
        var sequential = NewTable();
        var concurrent = NewTable();
        await using var sequentialClient = CreateClient(1);
        await using var concurrentClient = CreateClient(4);
        try
        {
            await CreateTableAsync(sequential.Table, withCheck: true);
            await CreateTableAsync(concurrent.Table, withCheck: true);
            static List<TimeRangeDataPointDto> Points()
            {
                var points = Enumerable.Range(0, Rows).Select(i => Point(i, i == 70_123 ? -1d : i, 2, DocumentDate)).ToList();
                points.Insert(70_124, Point(70_123, -2d, 2, DocumentDate));
                return points;
            }

            var sequentialFailure = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
                sequentialClient.Client.InsertTimeRangeDataAsync(sequential.Tenant, sequential.Table, UserColumns, Points(),
                    conflictPrecedence: Precedence));
            var concurrentFailure = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
                concurrentClient.Client.InsertTimeRangeDataAsync(concurrent.Tenant, concurrent.Table, UserColumns, Points(),
                    conflictPrecedence: Precedence));

            Assert.Equal(sequential.Table, sequentialFailure.QualifiedTable);
            Assert.Equal(concurrent.Table, concurrentFailure.QualifiedTable);
            Assert.Equal((2, 2), (sequentialFailure.RowsSent, sequentialFailure.RowsMissing));
            Assert.Equal((sequentialFailure.RowsSent, sequentialFailure.RowsMissing),
                (concurrentFailure.RowsSent, concurrentFailure.RowsMissing));
        }
        finally
        {
            await DropAsync(sequential.Table);
            await DropAsync(concurrent.Table);
        }
    }

    private sealed class OwnClient(CrateDatabaseClient client, ICrateDbConnectionAccess access) : IAsyncDisposable
    {
        public CrateDatabaseClient Client { get; } = client;
        public ValueTask DisposeAsync() => access.DisposeAsync();
    }

    private OwnClient CreateClient(int maxConcurrentInsertStatements)
    {
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = fixture.ConnectionString });
        var access = new CrateDbConnectionAccess(configuration, NullLogger<CrateDbConnectionAccess>.Instance);
        return new OwnClient(new CrateDatabaseClient(NullLogger<CrateDatabaseClient>.Instance, access, configuration,
            Options.Create(new CrateResilienceOptions()),
            Options.Create(new StreamDataInstanceConfiguration { MaxConcurrentInsertStatements = maxConcurrentInsertStatements }),
            Options.Create(new CrateRecomputeOptions())), access);
    }

    private static (string Tenant, string Table) NewTable()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        return (tenant, TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString()));
    }

    private async Task CreateTableAsync(string table, bool withCheck)
    {
        var ddl = ArchiveDdlGenerator.GenerateCreateWindowedTable(table, Columns, 2, 0);
        if (withCheck)
        {
            Assert.Contains(", PRIMARY KEY (", ddl, StringComparison.Ordinal);
            ddl = ddl.Replace(", PRIMARY KEY (", ", CONSTRAINT value_not_negative CHECK (\"value\" >= 0), PRIMARY KEY (",
                StringComparison.Ordinal);
        }

        await fixture.SqlAsync(ddl);
    }

    /// <summary>The stored rows as one line each, ordered by key; the write clock columns are left out.</summary>
    private async Task<List<string>> ReadAsync(string table)
    {
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var rows = await fixture.QueryAsync(
            $"SELECT {string.Join(", ", ComparedColumns.Select(c => $"\"{c}\""))} FROM {table} " +
            "ORDER BY \"window_start\", \"rtid\" LIMIT 1000000");
        return rows.Select(r => string.Join("|", ComparedColumns.Select(c => Format(r[c])))).ToList();
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        DateTime d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private Task DropAsync(string table) => fixture.SqlAsync($"DROP TABLE IF EXISTS {table}");
}
