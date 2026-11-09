using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataWriteLoss;

/// <summary>
///     AB#8679 against a real CrateDB whose query circuit breaker trips under parallel multi-row writes. CrateDB ends
///     the affected shard requests and answers the statement without an error, so before the fix the rows were lost
///     silently: a guarded write reported them as <see cref="StreamDataSkipReason.LowerPrecedence" />, an unguarded
///     write reported nothing. Now every write either stores what it reports or throws
///     <see cref="StreamDataWriteIncompleteException" />; no row is lost without an error.
/// </summary>
/// <remarks>
///     Each test runs <see cref="Rounds" /> rounds of <see cref="Writers" /> parallel writers with
///     <see cref="RowsPerWriter" /> rows each (three multi-row statements per writer), every writer into an archive of
///     its own. The verdict per writer reads the table after <c>REFRESH</c>. A test that saw no breaker trip proves
///     nothing and is skipped with that reason instead of passing.
/// </remarks>
[Collection(CrateMemoryPressureCollection.Name)]
public class ArchiveWriteUnderMemoryPressureIntegrationTests(CrateMemoryPressureFixture fixture)
{
    private const int Writers = 8;
    private const int RowsPerWriter = 2500;
    private const int Rounds = 3;

    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Origin = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveConflictKey[] Precedence =
    [
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
        new("dataquality", ConflictKeyOrder.LowerWins)
    ];

    private static readonly ArchiveColumnDdl[] Columns =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("DataQuality", new CrateColumnType.Primitive("INTEGER"), false, false),
        new("Note", new CrateColumnType.Primitive("TEXT"), false, false)
    ];

    private static readonly string[] UserColumns = ["value", "dataquality", "note"];

    // A long text per row makes each shard request large enough for the breaker.
    private static readonly string Note = new('x', 2000);

    private sealed record Outcome(int Writer, Exception? Error, IReadOnlyList<SkippedArchiveRow> Skipped);

    /// <summary>
    ///     Query breaker limits (share of the 512 MB heap) measured 2026-11-09 on crate 6.4.6: a guarded write (with
    ///     RETURNING) trips at 25 % in about half of its statements, an unguarded write needs 5 %; at the default (60 %)
    ///     neither trips with this heap. A limit where some writes pass and some trip tests both the retry and the
    ///     failure.
    /// </summary>
    private const string GuardedBreakerLimit = "25%";

    private const string UnguardedBreakerLimit = "5%";

    [Fact]
    public async Task GuardedWrites_StoreWhatTheyReport_OrFailLoudly()
    {
        await fixture.SetQueryBreakerLimitAsync(GuardedBreakerLimit);
        try
        {
            await GuardedWritesAsync();
        }
        finally
        {
            await fixture.ResetQueryBreakerLimitAsync();
        }
    }

    [Fact]
    public async Task UnguardedWrites_StoreEveryRow_OrFailLoudly()
    {
        await fixture.SetQueryBreakerLimitAsync(UnguardedBreakerLimit);
        try
        {
            await UnguardedWritesAsync();
        }
        finally
        {
            await fixture.ResetQueryBreakerLimitAsync();
        }
    }

    private async Task GuardedWritesAsync()
    {
        var tripsBefore = await fixture.BreakerTripsAsync();
        var failures = new List<string>();
        var loud = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var tenant = CrateStagingCopyFixture.NewTenantId();
            var tables = Enumerable.Range(0, Writers)
                .Select(_ => TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString())).ToArray();
            foreach (var table in tables)
            {
                await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table, Columns, 2, 0));
            }

            // First write: every key is new, so every row must be stored — the guard has nothing to decide.
            var first = await WriteInParallelAsync(tables, (w, i) => Window(i, value: i, quality: 2), tenant, guarded: true);
            foreach (var outcome in first)
            {
                if (outcome.Error is not null)
                {
                    loud += AssertLoud(outcome, failures);
                    continue;
                }

                if (outcome.Skipped.Count > 0)
                {
                    failures.Add($"round {round} writer {outcome.Writer}: first write reported {outcome.Skipped.Count} " +
                                 $"rows as skipped ({outcome.Skipped[0].Reason}) although every key was new");
                }

                var stored = await fixture.CountAsync($"SELECT count(*) FROM {tables[outcome.Writer]}");
                if (stored != RowsPerWriter)
                {
                    failures.Add($"round {round} writer {outcome.Writer}: first write succeeded, {stored} of {RowsPerWriter} rows stored");
                }
            }

            // Second write: even rows bring a better quality (1, they win), odd rows a worse one (3, they lose).
            var second = await WriteInParallelAsync(tables,
                (w, i) => Window(i, value: -1 - i, quality: i % 2 == 0 ? 1 : 3), tenant, guarded: true);
            foreach (var outcome in second)
            {
                if (outcome.Error is not null)
                {
                    loud += AssertLoud(outcome, failures);
                    continue;
                }

                if (first[outcome.Writer].Error is not null)
                {
                    continue; // the stored state of a failed first write is unknown; only its loudness is checked
                }

                var skipped = outcome.Skipped.Select(s => s.Index).ToHashSet();
                var rows = await fixture.QueryAsync($"SELECT \"window_start\", \"value\" FROM {tables[outcome.Writer]}");
                var valueByRow = rows.ToDictionary(
                    r => (int)(((DateTime)r["window_start"]!).ToUniversalTime() - Origin).TotalMinutes / 15,
                    r => Convert.ToDouble(r["value"], CultureInfo.InvariantCulture));
                var wrong = Enumerable.Range(0, RowsPerWriter).Count(i =>
                {
                    var expectStored = i % 2 == 0;
                    var reportedStored = !skipped.Contains(i);
                    var holdsNew = valueByRow.TryGetValue(i, out var v) && v.Equals(-1d - i);
                    return reportedStored != expectStored || holdsNew != expectStored;
                });
                if (wrong > 0)
                {
                    failures.Add($"round {round} writer {outcome.Writer}: second write succeeded, {wrong} rows reported or stored wrongly " +
                                 $"(skipped {skipped.Count}, expected {RowsPerWriter / 2})");
                }
            }
        }

        await SkipUnlessTheBreakerTripped(tripsBefore, loud);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task UnguardedWritesAsync()
    {
        var tripsBefore = await fixture.BreakerTripsAsync();
        var failures = new List<string>();
        var loud = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var tenant = CrateStagingCopyFixture.NewTenantId();
            var tables = Enumerable.Range(0, Writers)
                .Select(_ => TenantSchema.QualifiedArchiveTable(tenant, OctoObjectId.GenerateNewId().ToString())).ToArray();
            foreach (var table in tables)
            {
                await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(table, Columns, 2, 0));
            }

            var outcomes = await WriteInParallelAsync(tables, (w, i) => Window(i, value: i, quality: 2), tenant, guarded: false);
            foreach (var outcome in outcomes)
            {
                if (outcome.Error is not null)
                {
                    loud += AssertLoud(outcome, failures);
                    continue;
                }

                var stored = await fixture.CountAsync($"SELECT count(*) FROM {tables[outcome.Writer]}");
                if (stored != RowsPerWriter)
                {
                    failures.Add($"round {round} writer {outcome.Writer}: write succeeded, {stored} of {RowsPerWriter} rows stored");
                }
            }
        }

        await SkipUnlessTheBreakerTripped(tripsBefore, loud);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static int AssertLoud(Outcome outcome, List<string> failures)
    {
        if (outcome.Error is StreamDataWriteIncompleteException incomplete)
        {
            Assert.Contains("archive table", incomplete.Message, StringComparison.Ordinal);
            Assert.True(incomplete.RowsMissing > 0);
            return 1;
        }

        // Any other error is loud as well, but not the one this test is about: name it.
        failures.Add($"writer {outcome.Writer}: {outcome.Error!.GetType().Name}: {outcome.Error.Message}");
        return 1;
    }

    private async Task SkipUnlessTheBreakerTripped(int tripsBefore, int loud)
    {
        var trips = await fixture.BreakerTripsAsync() - tripsBefore;
        TestContext.Current.SendDiagnosticMessage($"AB#8679 stress: {trips} breaker trips, {loud} writes failed loudly");
        if (trips == 0)
        {
            Assert.Skip("The CrateDB memory breaker never tripped; the test proves nothing on this machine.");
        }
    }

    private async Task<Outcome[]> WriteInParallelAsync(string[] tables, Func<int, int, Row> row, string tenant, bool guarded)
    {
        var tasks = tables.Select((table, writer) => Task.Run(async () =>
        {
            try
            {
                var points = Enumerable.Range(0, RowsPerWriter).Select(i => row(writer, i)).ToList();
                var skipped = guarded
                    ? await fixture.Client.InsertTimeRangeDataAsync(tenant, table, UserColumns,
                        points.Select(p => p.ToTimeRange()), conflictPrecedence: Precedence)
                    : await fixture.Client.InsertDataAsync(tenant, table, UserColumns, points.Select(p => p.ToRaw()));
                return new Outcome(writer, null, skipped);
            }
            catch (Exception ex)
            {
                return new Outcome(writer, ex, []);
            }
        })).ToArray();
        var outcomes = await Task.WhenAll(tasks);
        foreach (var table in tables)
        {
            await fixture.SqlAsync($"REFRESH TABLE {table}");
        }

        return outcomes;
    }

    private static readonly OctoObjectId Meter = new("65f000000000000000000001");

    private sealed record Row(int Index, double Value, int Quality)
    {
        private Dictionary<string, object?> Attributes() => new()
        {
            ["value"] = Value, ["dataquality"] = Quality, ["note"] = Note
        };

        public TimeRangeDataPointDto ToTimeRange() => new(Attributes())
        {
            RtId = Meter,
            CkTypeId = Type,
            From = Origin.AddMinutes(15 * Index),
            To = Origin.AddMinutes(15 * (Index + 1))
        };

        public DataPointDto ToRaw() => new(Attributes())
        {
            RtId = Meter,
            CkTypeId = Type,
            Timestamp = Origin.AddMinutes(15 * Index)
        };
    }

    private static Row Window(int index, double value, int quality) => new(index, value, quality);
}
