using System.Diagnostics;
using System.Globalization;
using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#5967 (slice AB#6967) against the S3 store and the CrateDB of the local kind infrastructure
///     (octo-tools): a table dumped through <c>COPY TO</c> into SeaweedFS and loaded back, the credential kept out
///     of the job log, and — behind an environment variable — the measurement with the local copy of a large tenant.
///     Skipped when the kind cluster, its CrateDB or its store is not there. Reads the cluster, creates tables in a
///     schema of its own and objects under a prefix of its own, and removes both.
/// </summary>
[Collection(LocalKindStagingCollection.Name)]
public class StagingCopyLocalS3Tests(LocalKindStagingFixture fixture)
{
    private const string MeasureTenantVariable = "OCTO_STAGING_MEASURE_SOURCE_TENANT";
    private const string MeasureReportVariable = "OCTO_STAGING_MEASURE_REPORT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<ArchiveCopySliceResult>> Collect(IAsyncEnumerable<ArchiveCopySliceResult> results)
    {
        var list = new List<ArchiveCopySliceResult>();
        await foreach (var result in results)
        {
            list.Add(result);
        }

        return list;
    }

    private void RequireStore() => Assert.SkipUnless(fixture.Available, $"The local kind infrastructure is not usable: {fixture.Reason}");

    [Fact]
    public async Task ATableRoundTripsThroughTheLocalStore_AndTheJobLogHoldsNoCopy()
    {
        RequireStore();
        var tenantA = CrateStagingCopyFixture.NewTenantId();
        var tenantB = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        var prefix = $"agent-tests/{Guid.NewGuid():N}";
        var tableA = TenantSchema.QualifiedArchiveTable(tenantA, source.ToString());
        var tableB = TenantSchema.QualifiedArchiveTable(tenantB, target.ToString());
        var started = Convert.ToInt64(await fixture.ScalarAsync("SELECT CAST(CURRENT_TIMESTAMP AS BIGINT)"), CultureInfo.InvariantCulture);

        try
        {
            var ddl = ArchiveDdlGenerator.GenerateCreateTable(tableA,
                [new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)], 2, 0);
            await fixture.SqlAsync(ddl);
            await fixture.SqlAsync(ddl.Replace(tableA, tableB, StringComparison.Ordinal));
            await fixture.SqlAsync(
                $"INSERT INTO {tableA} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
                "SELECT 'rt1', CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'Test/Sensor-1', generate_series * 1.5 " +
                "FROM generate_series(0, 2999)");
            await fixture.SqlAsync($"REFRESH TABLE {tableA}");

            var repoA = fixture.NewRepository(tenantA, windowed: false, source);
            var repoB = fixture.NewRepository(tenantB, windowed: false, target);
            var location = fixture.Store.Location(prefix);
            var options = new ArchiveCopyOptions { TargetSliceRows = 1000 };

            var plan = await repoA.PlanArchiveCopyAsync(source, options, Ct);
            var dumped = await Collect(repoA.CopyToStagingAsync(source, plan, location, options, Ct));
            var objects = await fixture.Store.ListAsync(prefix, Ct);
            var loaded = await Collect(repoB.CopyFromStagingAsync(target, plan, location, options, Ct));

            Assert.True(plan.Slices.Count >= 3, $"{plan.Slices.Count} slices");
            Assert.All(dumped, r => Assert.True(r.IsSuccess, r.Message));
            Assert.Equal(plan.Slices.Select(s => s.RowCount), dumped.Select(r => r.CopiedRows));
            foreach (var slice in plan.Slices)
            {
                var folder = $"{prefix}/{slice.GetStagingPath(source)}/";
                Assert.Contains(objects, o => o.Key.StartsWith(folder, StringComparison.Ordinal) && o.Size > 0);
            }

            Assert.All(loaded, r => Assert.True(r.IsSuccess, r.Message));
            Assert.Equal(plan.Slices.Select(s => s.RowCount), loaded.Select(r => r.CopiedRows));
            Assert.Equal(3000, Convert.ToInt64(await fixture.ScalarAsync($"SELECT COUNT(*) FROM {tableB}"), CultureInfo.InvariantCulture));
            Assert.Equal(
                (await fixture.QueryAsync($"SELECT SUM(\"value\") AS s, MIN(\"timestamp\") AS lo, MAX(\"timestamp\") AS hi FROM {tableA}")).Single().Values,
                (await fixture.QueryAsync($"SELECT SUM(\"value\") AS s, MIN(\"timestamp\") AS lo, MAX(\"timestamp\") AS hi FROM {tableB}")).Single().Values);

            // the filter kept the COPY statements (with the real credential) out of the job log; others are recorded
            await Task.Delay(1500, Ct);
            Assert.Equal(0, Convert.ToInt64(await fixture.ScalarAsync(
                $"SELECT COUNT(*) FROM sys.jobs_log WHERE started >= {started} AND stmt ILIKE 'copy %'"), CultureInfo.InvariantCulture));
            Assert.True(Convert.ToInt64(await fixture.ScalarAsync(
                $"SELECT COUNT(*) FROM sys.jobs_log WHERE started >= {started} AND stmt ILIKE 'select%'"), CultureInfo.InvariantCulture) > 0,
                "no ordinary statement was recorded, the job log check proves nothing");
        }
        finally
        {
            await fixture.SqlAsync($"DROP TABLE IF EXISTS {tableA}");
            await fixture.SqlAsync($"DROP TABLE IF EXISTS {tableB}");
            await fixture.Store.DeletePrefixAsync(prefix, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AWrongKeyAtTheLocalStore_FailsTheSliceWithoutShowingTheKey()
    {
        RequireStore();
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        var prefix = $"agent-tests/{Guid.NewGuid():N}";

        try
        {
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(table,
                [new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)], 2, 0));
            await fixture.SqlAsync(
                $"INSERT INTO {table} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
                "SELECT 'rt1', CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'Test/Sensor-1', generate_series " +
                "FROM generate_series(0, 99)");
            await fixture.SqlAsync($"REFRESH TABLE {table}");
            var repo = fixture.NewRepository(tenant, windowed: false, source);
            var wrong = new ArchiveCopyStagingLocation(
                $"s3://CANARYKEYID1234:CANARY/SECRET+value==@{fixture.Store.CrateEndpoint}/{fixture.Store.Bucket}/{prefix}",
                new Dictionary<string, string> { ["protocol"] = "http" });
            var plan = await repo.PlanArchiveCopyAsync(source, null, Ct);

            var results = await Collect(repo.CopyToStagingAsync(source, plan, wrong, null, Ct));

            var failed = Assert.Single(results);
            Assert.False(failed.IsSuccess);
            Assert.DoesNotContain("CANARY", failed.Message);
            Assert.DoesNotContain("SECRET+value", failed.Message);
            Assert.Empty(await fixture.Store.ListAsync(prefix, Ct));
        }
        finally
        {
            await fixture.SqlAsync($"DROP TABLE IF EXISTS {table}");
            await fixture.Store.DeletePrefixAsync(prefix, CancellationToken.None);
        }
    }

    // ---- the measurement ---------------------------------------------------------------------------------

    /// <summary>
    ///     Dumps every data table of a large local tenant into the local store and loads it into a tenant of its
    ///     own, then compares the two. Runs only when <c>OCTO_STAGING_MEASURE_SOURCE_TENANT</c> names the source
    ///     tenant; the source is only read. The figures go to the file named by <c>OCTO_STAGING_MEASURE_REPORT</c>.
    /// </summary>
    [Fact]
    public async Task Measurement_DumpAndLoadOfALargeLocalTenant()
    {
        RequireStore();
        var sourceTenant = Environment.GetEnvironmentVariable(MeasureTenantVariable);
        Assert.SkipUnless(!string.IsNullOrEmpty(sourceTenant), $"{MeasureTenantVariable} is not set");
        var reportPath = Environment.GetEnvironmentVariable(MeasureReportVariable);

        var targetTenant = "stgmeasure" + Guid.NewGuid().ToString("N")[..6];
        var prefix = $"agent-measure/{Guid.NewGuid():N}";
        var sourceSchema = TenantSchema.SchemaName(sourceTenant!);
        var targetSchema = TenantSchema.SchemaName(targetTenant);
        var tables = (await fixture.QueryAsync(
                "SELECT table_name FROM information_schema.tables " +
                $"WHERE table_schema = '{sourceSchema}' AND table_name LIKE 'archive\\_%' AND table_name NOT LIKE '%\\_\\_%' ORDER BY table_name"))
            .Select(r => (string)r["table_name"]!).ToList();
        Assert.NotEmpty(tables);

        var report = new StringBuilder();
        report.AppendLine($"source tenant schema '{sourceSchema}' (read only), target schema '{targetSchema}', prefix '{prefix}'");
        report.AppendLine($"CrateDB {await fixture.ScalarAsync("SELECT version['number'] FROM sys.nodes LIMIT 1")}, slice target {ArchiveCopyOptions.DefaultTargetSliceRows} rows");
        report.AppendLine("table | rows | staged MiB | plan s | dump s | dump rows/s | load s | merge s | load rows/s (load+merge) | identical");

        long totalRows = 0, totalBytes = 0;
        double totalDump = 0, totalLoad = 0, totalMerge = 0;
        var verdict = new List<string>();
        try
        {
            foreach (var tableName in tables)
            {
                var rtIdText = tableName["archive_".Length..];
                var rt = OctoObjectId.Parse(rtIdText);
                var sourceTable = $"\"{sourceSchema}\".\"{tableName}\"";
                var targetTable = $"\"{targetSchema}\".\"{tableName}\"";

                var ddl = (string)(await fixture.ScalarAsync($"SHOW CREATE TABLE {sourceTable}"))!;
                await fixture.SqlAsync(ddl.Replace($"\"{sourceSchema}\".", $"\"{targetSchema}\".", StringComparison.Ordinal));

                var repoSource = fixture.NewRepository(sourceTenant!, windowed: true, rt);
                var repoTarget = fixture.NewRepository(targetTenant, windowed: true, rt);
                var location = fixture.Store.Location(prefix);
                var options = new ArchiveCopyOptions();

                var watch = Stopwatch.StartNew();
                var plan = await repoSource.PlanArchiveCopyAsync(rt, options, Ct);
                var planSeconds = watch.Elapsed.TotalSeconds;

                watch.Restart();
                var dumped = await Collect(repoSource.CopyToStagingAsync(rt, plan, location, options, Ct));
                var dumpSeconds = watch.Elapsed.TotalSeconds;
                Assert.All(dumped, r => Assert.True(r.IsSuccess, r.Message));
                Assert.Equal(plan.TotalRows, dumped.Sum(r => r.CopiedRows));

                var objects = await fixture.Store.ListAsync($"{prefix}/{rtIdText}/", Ct);
                var withBytes = plan with
                {
                    Slices = plan.Slices.Select(s => s.WithStagedBytes(objects
                        .Where(o => o.Key.StartsWith($"{prefix}/{s.GetStagingPath(rt)}/", StringComparison.Ordinal)).Sum(o => o.Size))).ToList()
                };
                var stagedBytes = objects.Sum(o => o.Size);

                // the load: time to the last slice, then the rest (settings, refresh, segment merge)
                var loadWatch = Stopwatch.StartNew();
                double loadSeconds = 0;
                var loaded = new List<ArchiveCopySliceResult>();
                await foreach (var result in repoTarget.CopyFromStagingAsync(rt, withBytes, location, options, Ct))
                {
                    loaded.Add(result);
                    loadSeconds = loadWatch.Elapsed.TotalSeconds;
                }

                var totalLoadSeconds = loadWatch.Elapsed.TotalSeconds;
                Assert.All(loaded, r => Assert.True(r.IsSuccess, r.Message));
                Assert.Equal(plan.Slices.Select(s => s.RowCount), loaded.Select(r => r.CopiedRows));
                Assert.All(loaded, r => Assert.Equal(0, r.ErrorRows));

                var identical = await SameDataAsync(sourceTable, targetTable);
                verdict.Add($"{tableName}: {(identical ? "identical" : "DIFFERENT")}");
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{tableName} | {plan.TotalRows} | {stagedBytes / 1048576.0:0.0} | {planSeconds:0.0} | {dumpSeconds:0.0} | {plan.TotalRows / Math.Max(dumpSeconds, 0.001):0} | {loadSeconds:0.0} | {totalLoadSeconds - loadSeconds:0.0} | {plan.TotalRows / Math.Max(totalLoadSeconds, 0.001):0} | {identical}"));

                totalRows += plan.TotalRows;
                totalBytes += stagedBytes;
                totalDump += dumpSeconds;
                totalLoad += loadSeconds;
                totalMerge += totalLoadSeconds - loadSeconds;
                Assert.True(identical, $"{tableName}: source and copy differ");
            }
        }
        finally
        {
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"TOTAL | {totalRows} | {totalBytes / 1048576.0:0.0} | | {totalDump:0.0} | {totalRows / Math.Max(totalDump, 0.001):0} | {totalLoad:0.0} | {totalMerge:0.0} | {totalRows / Math.Max(totalLoad + totalMerge, 0.001):0} |"));
            foreach (var table in tables)
            {
                await fixture.SqlAsync($"DROP TABLE IF EXISTS \"{targetSchema}\".\"{table}\"");
            }

            var deleted = await fixture.Store.DeletePrefixAsync(prefix, CancellationToken.None);
            report.AppendLine($"cleanup: dropped {tables.Count} target tables in schema '{targetSchema}', deleted {deleted} objects under '{prefix}'");
            if (!string.IsNullOrEmpty(reportPath))
            {
                await File.WriteAllTextAsync(reportPath, report.ToString(), CancellationToken.None);
            }
        }
    }

    /// <summary>Row count, time bounds and the sums of all numeric columns per month are the same in both tables.</summary>
    private async Task<bool> SameDataAsync(string sourceTable, string targetTable)
    {
        var schemaAndName = sourceTable.Replace("\"", string.Empty, StringComparison.Ordinal).Split('.');
        var numeric = (await fixture.QueryAsync(
                "SELECT column_name FROM information_schema.columns " +
                $"WHERE table_schema = '{schemaAndName[0]}' AND table_name = '{schemaAndName[1]}' " +
                "AND data_type IN ('double precision', 'integer', 'bigint', 'real') ORDER BY ordinal_position"))
            .Select(r => (string)r["column_name"]!).ToList();
        var sums = string.Concat(numeric.Select(c => $", SUM(\"{c}\") AS \"s_{c}\""));
        string Sql(string table) =>
            $"SELECT date_trunc('month', \"window_start\") AS m, COUNT(*) AS c, MIN(\"window_start\") AS lo, MAX(\"window_end\") AS hi{sums} " +
            $"FROM {table} GROUP BY 1 ORDER BY 1";

        var left = await fixture.QueryAsync(Sql(sourceTable));
        var right = await fixture.QueryAsync(Sql(targetTable));
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            foreach (var (key, a) in left[i])
            {
                var b = right[i][key];
                var equal = a is double da && b is double db
                    ? Math.Abs(da - db) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(da), Math.Abs(db)))
                    : Equals(a, b);
                if (!equal)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
