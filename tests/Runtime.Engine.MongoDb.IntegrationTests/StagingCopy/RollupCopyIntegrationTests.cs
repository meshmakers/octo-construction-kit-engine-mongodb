using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#5967 slice 2 (AB#6968) against a real CrateDB: a rollup with its generation map is dumped and loaded into a
///     fresh table pair, the repeat rule when a pointer flips during the dump, and the row-count verification.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupCopyIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Row count, sum and time bounds per generation: what a restored rollup must have in common with its source.</summary>
    private async Task<List<string>> FingerprintAsync(string tenantId, OctoObjectId rollup)
    {
        var rows = await fixture.QueryAsync(
            "SELECT \"generation\" AS g, COUNT(*) AS c, SUM(\"value\") AS s, MIN(\"window_start\") AS lo, MAX(\"window_start\") AS hi " +
            $"FROM {TenantSchema.QualifiedArchiveTable(tenantId, rollup.ToString())} GROUP BY 1 ORDER BY 1");
        return rows.Select(r => string.Join("|", r.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).ToList();
    }

    private async Task<List<string>> PointersAsync(string tenantId, OctoObjectId rollup)
    {
        var genMap = GenerationMapSqlBuilder.GenMapTable(tenantId, rollup.ToString());
        await fixture.SqlAsync($"REFRESH TABLE {genMap}");
        var rows = await fixture.QueryAsync(
            $"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {genMap} ORDER BY 1, 2, 3");
        return rows.Select(r => string.Join("|", r.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).ToList();
    }

    [Fact]
    public async Task DumpAndLoad_ARollupWith16Generations_GivesTheSamePointersAndPerGenerationRowsSumsAndBounds()
    {
        var tenantA = CrateStagingCopyFixture.NewTenantId();
        var tenantB = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRollupAsync(tenantA, source, generations: 16);
        await fixture.CreateRollupAsync(tenantB, target);
        var repoA = fixture.NewRollupRepository(tenantA, source);
        var repoB = fixture.NewRollupRepository(tenantB, target);
        var location = CrateStagingCopyFixture.NewFileLocation();
        var options = new ArchiveCopyOptions { TargetSliceRows = 100 };

        // the plan is what the dump job would write into the manifest; the folders must exist for a file:// location
        var plan = await repoA.PlanArchiveCopyAsync(source, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        var dump = await repoA.DumpArchiveToStagingAsync(source, location, options, Ct);
        var load = await repoB.LoadArchiveFromStagingAsync(target, dump.Plan, location, options, Ct);

        Assert.True(dump.IsSuccess);
        Assert.Equal(1, dump.Attempts);
        Assert.NotNull(dump.MapReadAtUtc);
        Assert.True(dump.MapReadAtUtc <= dump.LastDataCopiedAtUtc);
        Assert.Equal(16, dump.Plan.Slices.Single(s => s.Table == ArchiveCopyTable.GenerationMap).RowCount);
        Assert.Equal(480 + 16 * 24, dump.Plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).Sum(s => s.RowCount));

        Assert.True(load.IsSuccess);
        Assert.True(load.RowCountVerified);
        Assert.Equal(load.ExpectedRows, load.LoadedRows);

        var pointers = await PointersAsync(tenantA, source);
        Assert.Equal(16, pointers.Count);
        Assert.Equal(pointers, await PointersAsync(tenantB, target)); // identical pointer entries, scope included
        var fingerprint = await FingerprintAsync(tenantA, source);
        Assert.Equal(17, fingerprint.Count); // generation 0 and 16 recomputed ones
        Assert.Equal(fingerprint, await FingerprintAsync(tenantB, target));
    }

    [Fact]
    public async Task Dump_OfARollupWhoseMapMovesDuringTheCopy_RepeatsAndTheFinalMapAndDataAgree()
    {
        var tenantA = CrateStagingCopyFixture.NewTenantId();
        var tenantB = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRollupAsync(tenantA, source, generations: 4);
        await fixture.CreateRollupAsync(tenantB, target);
        var repoB = fixture.NewRollupRepository(tenantB, target);
        var location = CrateStagingCopyFixture.NewFileLocation();
        // One data slice (its key comes from the table's bounds, which the flip does not change), so the folder
        // prepared for the first attempt serves the repeat as well - a file:// location needs its folders in advance.
        var options = new ArchiveCopyOptions();
        var genMap = GenerationMapSqlBuilder.GenMapTable(tenantA, source.ToString());
        var table = TenantSchema.QualifiedArchiveTable(tenantA, source.ToString());

        // A recompute flips the pointer of day 1 to generation 5 right after the first data slice was copied: the
        // client below runs it at exactly that moment, so the test does not depend on timing.
        var client = new FlipOnFirstDataCopyClient(fixture.Client, async () =>
        {
            await fixture.SqlAsync(
                $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value\") " +
                $"SELECT window_start, window_end, rtid, cktypeid, FALSE, 5, value + 7 FROM {table} WHERE generation = 1");
            await fixture.SqlAsync($"REFRESH TABLE {table}");
            await fixture.SqlAsync(GenerationMapSqlBuilder.BuildUpsertPointer(genMap,
                CrateStagingCopyFixture.RollupOrigin, CrateStagingCopyFixture.RollupOrigin.AddHours(24), "", 5));
        });
        var runner = new StagingCopyRunner(NullLogger.Instance, tenantA, client);
        var dumper = new RollupArchiveDumper(NullLogger.Instance, tenantA, client, runner, TimeProvider.System);
        var sourceTarget = new ArchiveCopyTarget(source, TenantSchema.SchemaName(tenantA),
            TenantSchema.ArchiveTableName(source.ToString()), "window_start", TenantSchema.ArchiveTableName(source.ToString()) + "__genmap");
        var plan = await runner.PlanAsync(sourceTarget, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);

        var dump = await dumper.DumpAsync(sourceTarget, location, options, Ct);

        Assert.True(client.Flipped);
        Assert.Equal(2, dump.Attempts); // the map moved in the first attempt, the second one found it still
        Assert.True(dump.IsSuccess);
        Assert.Equal(480 + 4 * 24 + 24, dump.Plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).Sum(s => s.RowCount)); // incl. generation 5

        var load = await repoB.LoadArchiveFromStagingAsync(target, dump.Plan, location, options, Ct);

        Assert.True(load.RowCountVerified);
        Assert.Equal(await PointersAsync(tenantA, source), await PointersAsync(tenantB, target));
        Assert.Equal(await FingerprintAsync(tenantA, source), await FingerprintAsync(tenantB, target));
        var restoredPointers = await PointersAsync(tenantB, target);
        Assert.Contains(restoredPointers, p => p.EndsWith("||5", StringComparison.Ordinal));
        // every restored pointer names a generation that has rows in the restored table
        foreach (var generation in restoredPointers.Select(p => p.Split('|')[^1]).Distinct())
        {
            Assert.Contains(await FingerprintAsync(tenantB, target), f => f.StartsWith(generation + "|", StringComparison.Ordinal));
        }
    }

    /// <summary>Runs <c>flip</c> once, after the first COPY TO of a data slice has been executed.</summary>
    private sealed class FlipOnFirstDataCopyClient(ICrateCopyClient inner, Func<Task> flip) : ICrateCopyClient
    {
        public bool Flipped { get; private set; }

        public async Task<CrateStatementResult> ExecuteStatementAsync(
            string tenantId, CopyStatement statement, TimeSpan commandTimeout, CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteStatementAsync(tenantId, statement, commandTimeout, cancellationToken);
            var sql = statement.Redacted;
            if (!Flipped && sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains(" TO DIRECTORY ", StringComparison.Ordinal)
                && !sql.Contains("__genmap", StringComparison.Ordinal))
            {
                Flipped = true;
                await flip();
            }

            return result;
        }
    }
}
