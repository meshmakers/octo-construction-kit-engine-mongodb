using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class RollupComputedColumnEvalTests
{
    private static readonly DateTime BucketStart = new(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BucketEnd = new(2026, 5, 11, 15, 0, 0, DateTimeKind.Utc);
    private const string Table = "\"acmecorp\".\"energy_rollup\"";

    // ---- Pure SQL builder ----

    [Fact]
    public void BuildSelect_EmitsKeysAndAggregateColumns()
    {
        var sql = RollupComputedColumnSqlBuilder.BuildSelect(
            Table, new[] { "active_sum", "active_count" }, BucketStart, BucketEnd);

        Assert.Equal(
            "SELECT \"rtid\", \"cktypeid\", \"active_sum\", \"active_count\" FROM \"acmecorp\".\"energy_rollup\" " +
            "WHERE \"window_start\" = '2026-05-11T14:00:00.000Z'::timestamp with time zone " +
            "AND \"window_end\" = '2026-05-11T15:00:00.000Z'::timestamp with time zone;",
            sql);
    }

    [Fact]
    public void BuildUpdate_FormatsLiteralsByType()
    {
        var sql = RollupComputedColumnSqlBuilder.BuildUpdate(
            Table,
            new (string, object?)[] { ("ratio", 0.8d), ("flag", true), ("missing", null) },
            "rt1", "Energy/Meter", BucketStart, BucketEnd);

        Assert.Equal(
            "UPDATE \"acmecorp\".\"energy_rollup\" SET \"ratio\" = 0.8, \"flag\" = TRUE, \"missing\" = NULL " +
            "WHERE \"window_start\" = '2026-05-11T14:00:00.000Z'::timestamp with time zone " +
            "AND \"window_end\" = '2026-05-11T15:00:00.000Z'::timestamp with time zone " +
            "AND \"rtid\" = 'rt1' AND \"cktypeid\" = 'Energy/Meter';",
            sql);
    }

    [Fact]
    public void BuildUpdate_EscapesStringKeys()
    {
        var sql = RollupComputedColumnSqlBuilder.BuildUpdate(
            Table, new (string, object?)[] { ("x", 1L) }, "r't1", "T", BucketStart, BucketEnd);

        Assert.Contains("\"rtid\" = 'r''t1'", sql);
    }

    // ---- End-to-end via AggregateBucketAsync ----

    private static readonly OctoObjectId RollupRt = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId SourceRt = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("EnergyMeterRollup"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, _db, _mgmt, Config, "tenant-x", _store, Formula);

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        params IReadOnlyDictionary<string, object?>[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task AggregateBucket_EvaluatesRollupComputedColumns_AndUpdatesRow()
    {
        var rollup = new RollupArchiveSnapshot(
            RollupRt, Type, CkArchiveStatus.Activated, null, SourceRt,
            TimeSpan.FromHours(1), TimeSpan.Zero, null,
            new[] { new CkRollupAggregationSpec("active", CkRollupFunction.Sum, null) },
            null);

        var source = new ArchiveSnapshot(SourceRt, Type, CkArchiveStatus.Activated, null,
            new[] { new CkArchiveColumnSpec("active", true, false) });

        // The rollup's own snapshot (loaded by the eval pass) carries the aggregate column + a
        // computed column over it.
        var rollupArchiveSnapshot = new ArchiveSnapshot(RollupRt, Type, CkArchiveStatus.Activated, null,
            new CkArchiveColumnSpec[]
            {
                new("active_sum", true, false),
                new(string.Empty, Indexed: true, Required: false)
                {
                    Name = "ratio",
                    Formula = "active_sum / 2",
                    ResultType = FormulaResultType.Double,
                },
            })
        {
            RollupAggregations = new[] { new CkRollupAggregationSpec("active", CkRollupFunction.Sum, null) },
        };
        A.CallTo(() => _store.GetAsync(RollupRt)).Returns(rollupArchiveSnapshot);

        // Bucket row read back after the aggregate INSERT.
        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Returns(Rows(new Dictionary<string, object?>
            {
                ["rtid"] = "rt1",
                ["cktypeid"] = "EnergyMeterRollup",
                ["active_sum"] = 10.0,
            }));

        var executed = new List<string>();
        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Invokes(call => executed.Add(call.GetArgument<string>(1)!))
            .Returns(1);

        await NewSut().AggregateBucketAsync(source, rollup, BucketStart, BucketEnd, CancellationToken.None);

        var update = Assert.Single(executed, s => s.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Contains("\"ratio\" = 5", update);     // 10 / 2
        Assert.Contains("\"rtid\" = 'rt1'", update);
    }

    [Fact]
    public async Task AggregateBucket_NoComputedColumns_DoesNotReadBackOrUpdate()
    {
        var rollup = new RollupArchiveSnapshot(
            RollupRt, Type, CkArchiveStatus.Activated, null, SourceRt,
            TimeSpan.FromHours(1), TimeSpan.Zero, null,
            new[] { new CkRollupAggregationSpec("active", CkRollupFunction.Sum, null) },
            null);
        var source = new ArchiveSnapshot(SourceRt, Type, CkArchiveStatus.Activated, null,
            new[] { new CkArchiveColumnSpec("active", true, false) });

        // Rollup snapshot with only the aggregate column — no computed columns.
        A.CallTo(() => _store.GetAsync(RollupRt)).Returns(
            new ArchiveSnapshot(RollupRt, Type, CkArchiveStatus.Activated, null,
                new[] { new CkArchiveColumnSpec("active_sum", true, false) })
            {
                RollupAggregations = new[] { new CkRollupAggregationSpec("active", CkRollupFunction.Sum, null) },
            });

        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._)).Returns(1);

        await NewSut().AggregateBucketAsync(source, rollup, BucketStart, BucketEnd, CancellationToken.None);

        // No read-back happened (only the aggregate INSERT ran).
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
