using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>The physical table of an archive the staging copy works on.</summary>
/// <param name="ArchiveRtId">Runtime id of the archive.</param>
/// <param name="Schema">Tenant schema (unquoted).</param>
/// <param name="TableName">Table name (unquoted).</param>
/// <param name="TimeColumn"><c>window_start</c> on a windowed archive, <c>timestamp</c> on a raw one.</param>
internal sealed record ArchiveCopyTarget(OctoObjectId ArchiveRtId, string Schema, string TableName, string TimeColumn)
{
    public string QualifiedTable => $"\"{Schema}\".\"{TableName}\"";
}

/// <summary>
/// The native copy of one archive's data table to and from a staging location with CrateDB's own bulk
/// statements (AB#5967, concept <c>concept-tenant-backup-native-archive-dump.md</c> §6 and §7): planning in
/// counted time slices, <c>COPY TO</c> per slice, and <c>COPY FROM</c> per slice with the checks, bulk table
/// settings and segment merge around it. Everything it sends runs through <see cref="ICrateCopyClient"/>.
/// </summary>
/// <remarks>
/// <para>The credential of the staging location stays inside <see cref="CopyStatement"/>: log lines, result
/// messages and exceptions of this class carry only redacted text.</para>
/// <para>Generation maps of rollups are not copied yet (slice 2 of the feature): a plan has data slices only
/// and a plan with a generation-map slice is refused.</para>
/// </remarks>
internal sealed class StagingCopyRunner(ILogger logger, string tenantId, ICrateCopyClient client)
{
    /// <summary>
    /// A loaded table takes about nine times its gzip size right after the load and about six and a half times
    /// after the segment merge (concept §7, measured with the 115 million row tenant).
    /// </summary>
    internal const int DiskSpaceFactor = 9;

    private const int MaxErrorTextCharacters = 300;

    // ---- planning ----------------------------------------------------------------------------------------

    /// <summary>
    /// Cuts the data table into counted slices of about <see cref="ArchiveCopyOptions.TargetSliceRows"/> rows
    /// (the planner of the row export: propose, count, accept or narrow). A table that does not exist, or is
    /// empty, yields a plan without slices.
    /// </summary>
    public async Task<ArchiveCopyPlan> PlanAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var description = await DescribeAsync(target, ArchiveCopyTable.Data, options, cancellationToken);
        if (description.Columns.Count == 0)
        {
            return new ArchiveCopyPlan(target.ArchiveRtId, [description], []);
        }

        var bounds = await ReadBoundsAsync(target, options, cancellationToken);
        var slices = new List<ArchiveCopySlice>();
        if (bounds is { } b)
        {
            var planner = new ExportSlicePlanner(b.FromInclusiveMs, b.ToExclusiveMs, b.RowCount, options.TargetSliceRows);
            while (planner.TryNext(out var fromMs, out var toMs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fromUtc = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;
                var toUtc = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;

                var rows = await CountAsync(CrateDbStreamDataRepository.BuildExportSliceCountSql(
                    target.QualifiedTable, target.TimeColumn, fromUtc, toUtc, toInclusive: false), options, cancellationToken);
                if (!planner.Accept(rows) || rows == 0)
                {
                    continue; // too large: proposed again narrower; empty: nothing to copy
                }

                slices.Add(ArchiveCopySlice.ForTimeRange(fromUtc, toUtc, rows));
            }
        }

        logger.LogInformation(
            "Planned the native copy of archive {ArchiveRtId} of tenant {TenantId}: {Slices} slices, {Rows} rows.",
            target.ArchiveRtId, tenantId, slices.Count, slices.Sum(s => s.RowCount));
        return new ArchiveCopyPlan(target.ArchiveRtId, [description], slices);
    }

    // ---- description -------------------------------------------------------------------------------------

    /// <summary>Every column of the table with its CrateDB data type; empty when the table does not exist.</summary>
    public async Task<ArchiveTableDescription> DescribeAsync(
        ArchiveCopyTarget target, ArchiveCopyTable table, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var tableName = table == ArchiveCopyTable.Data ? target.TableName : target.TableName + "__genmap";
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT column_name, data_type FROM information_schema.columns " +
            $"WHERE table_schema = {CopyStatement.Literal(target.Schema)} AND table_name = {CopyStatement.Literal(tableName)} " +
            "ORDER BY ordinal_position", returnsRows: true), options.CommandTimeout, cancellationToken);

        var columns = result.Rows
            .Select(r => new ArchiveCopyColumn(Text(r, "column_name"), Text(r, "data_type")))
            .ToList();
        return new ArchiveTableDescription(target.ArchiveRtId, table, columns);
    }

    /// <summary>The version of the CrateDB nodes (several, comma-separated, during a rolling upgrade).</summary>
    public async Task<string> GetStorageVersionAsync(ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT DISTINCT version['number'] AS version FROM sys.nodes ORDER BY 1", returnsRows: true),
            options.CommandTimeout, cancellationToken);
        return string.Join(",", result.Rows.Select(r => Text(r, "version")).Where(v => v.Length > 0));
    }

    // ---- COPY TO -----------------------------------------------------------------------------------------

    /// <summary>Dumps the slices of <paramref name="plan"/> one at a time; see <see cref="IStreamDataRepository.CopyToStagingAsync"/>.</summary>
    public async IAsyncEnumerable<ArchiveCopySliceResult> CopyToAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        EnsurePlanFits(target, plan, mustBeSameArchive: true);
        await JobLogFilterGuard.EnsureFilteredAsync(client, tenantId, options, target.ArchiveRtId, cancellationToken);

        foreach (var slice in plan.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CopySliceToAsync(target, plan, slice, location, options, cancellationToken);
            yield return result;
            if (!result.IsSuccess)
            {
                yield break; // earlier slices stay valid; the caller decides what to do with the archive
            }
        }
    }

    private async Task<ArchiveCopySliceResult> CopySliceToAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopySlice slice, ArchiveCopyStagingLocation location,
        ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var sliceLocation = location.WithSubPath(slice.GetStagingPath(plan.ArchiveRtId));
        var statement = CopyStatement.CopyTo(target.QualifiedTable, target.TimeColumn,
            slice.FromUtc!.Value, slice.ToUtc!.Value, sliceLocation);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await client.ExecuteStatementAsync(tenantId, statement, options.CommandTimeout, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var warning = outcome.AffectedRows == slice.RowCount
                ? null
                : $"Counted {slice.RowCount} rows before the copy, copied {outcome.AffectedRows}.";
            if (warning is not null)
            {
                // The tenant stays online while it is dumped, so the open bucket may have changed.
                logger.LogWarning("Archive {ArchiveRtId}, slice {Slice}: {Warning}", target.ArchiveRtId, slice.Key, warning);
            }

            return new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Succeeded, outcome.AffectedRows, 0, elapsed, warning);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            return Failed(target, slice, "dump", sliceLocation.Scrub(ex.Message), Stopwatch.GetElapsedTime(started));
        }
    }

    // ---- COPY FROM ---------------------------------------------------------------------------------------

    /// <summary>Loads the slices of <paramref name="plan"/> one at a time; see <see cref="IStreamDataRepository.CopyFromStagingAsync"/>.</summary>
    public async IAsyncEnumerable<ArchiveCopySliceResult> CopyFromAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        EnsurePlanFits(target, plan, mustBeSameArchive: false);
        await JobLogFilterGuard.EnsureFilteredAsync(client, tenantId, options, target.ArchiveRtId, cancellationToken);

        // Checks that need no data, before anything is changed.
        await EnsureColumnsExistAsync(target, plan, options, cancellationToken);
        await EnsureDiskSpaceAsync(target, plan, options, cancellationToken);
        logger.LogInformation("Loading archive {ArchiveRtId} of tenant {TenantId} into CrateDB {Version}.",
            target.ArchiveRtId, tenantId, await GetStorageVersionAsync(options, cancellationToken));

        var previous = await ReadTableSettingsAsync(target, options, cancellationToken);
        var settingsRestored = false;
        var allLoaded = true;
        await ApplyBulkSettingsAsync(target, options, cancellationToken);
        try
        {
            foreach (var slice in plan.Slices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await CopySliceFromAsync(target, plan, slice, location, options, cancellationToken);
                yield return result;
                if (!result.IsSuccess)
                {
                    allLoaded = false;
                    yield break;
                }
            }

            // Back to the normal settings before the merge, then the merge itself (concept §7, step 6).
            await RestoreSettingsAsync(target, previous, options, CancellationToken.None);
            settingsRestored = true;
            await FinishLoadAsync(target, plan, options, cancellationToken);
        }
        finally
        {
            // Also after a failed slice, an exception and a caller that walks away.
            if (!settingsRestored)
            {
                await RestoreSettingsSafelyAsync(target, previous, options);
            }

            if (!allLoaded)
            {
                logger.LogWarning("Archive {ArchiveRtId} of tenant {TenantId} was not loaded completely.",
                    target.ArchiveRtId, tenantId);
            }
        }
    }

    private async Task<ArchiveCopySliceResult> CopySliceFromAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopySlice slice, ArchiveCopyStagingLocation location,
        ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var sliceLocation = location.WithSubPath(slice.GetStagingPath(plan.ArchiveRtId));
        var statement = CopyStatement.CopyFrom(target.QualifiedTable, sliceLocation, options.OverwriteDuplicates);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await client.ExecuteStatementAsync(tenantId, statement, options.CommandTimeout, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);

            long success = 0, errors = 0;
            string? firstError = null;
            foreach (var row in outcome.Rows)
            {
                success += Long(row, "success_count");
                errors += Long(row, "error_count");
                firstError ??= errors > 0 ? Text(row, "errors") : null;
            }

            if (errors == 0 && success == slice.RowCount)
            {
                return new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Succeeded, success, 0, elapsed);
            }

            var reason = errors > 0
                ? $"{errors} rows were rejected while loading ({success} loaded, {slice.RowCount} expected)." +
                  (string.IsNullOrEmpty(firstError) ? string.Empty : " First error: " + Abbreviate(sliceLocation.Scrub(firstError)))
                : $"{success} rows were loaded, {slice.RowCount} were expected.";
            logger.LogWarning("Archive {ArchiveRtId}, slice {Slice} (load): {Reason}", target.ArchiveRtId, slice.Key, reason);
            return new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Failed, success, errors, elapsed, reason);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            return Failed(target, slice, "load", sliceLocation.Scrub(ex.Message), Stopwatch.GetElapsedTime(started));
        }
    }

    // ---- checks, bulk settings, finish -------------------------------------------------------------------

    private async Task EnsureColumnsExistAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var dumped = plan.Tables.FirstOrDefault(t => t.Table == ArchiveCopyTable.Data);
        if (dumped is null)
        {
            return;
        }

        var existing = (await DescribeAsync(target, ArchiveCopyTable.Data, options, cancellationToken))
            .Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = dumped.Columns.Select(c => c.Name).Where(c => !existing.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.ColumnMissing,
                $"The table of archive '{target.ArchiveRtId}' has no column {string.Join(", ", missing.Select(m => $"'{m}'"))} " +
                "that the dump contains. Nothing was loaded.", target.ArchiveRtId);
        }
    }

    private async Task EnsureDiskSpaceAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        if (plan.Slices.Count == 0 || plan.Slices.Any(s => s.StagedBytes is null))
        {
            logger.LogDebug("The sizes of the staged files of archive {ArchiveRtId} are not known; skipping the disk space check.",
                target.ArchiveRtId);
            return;
        }

        var expected = plan.Slices.Sum(s => s.StagedBytes!.Value) * DiskSpaceFactor;
        var nodes = (await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT name, fs['total']['available'] AS available FROM sys.nodes", returnsRows: true),
            options.CommandTimeout, cancellationToken)).Rows;
        if (nodes.Count == 0)
        {
            return;
        }

        var perNode = expected / nodes.Count; // no replicas while loading, the shards spread evenly
        var tightest = nodes.OrderBy(n => Long(n, "available")).First();
        var available = Long(tightest, "available");
        if (available < perNode)
        {
            throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.InsufficientDiskSpace,
                $"Loading archive '{target.ArchiveRtId}' needs about {FormatBytes(perNode)} per node " +
                $"({DiskSpaceFactor} times the {FormatBytes(expected / DiskSpaceFactor)} of staged files, spread over {nodes.Count} node(s)), " +
                $"but node '{Text(tightest, "name")}' has {FormatBytes(available)} available. Nothing was loaded.",
                target.ArchiveRtId);
        }
    }

    private async Task<TableSettings> ReadTableSettingsAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT number_of_replicas, settings['refresh_interval'] AS refresh_interval FROM information_schema.tables " +
            $"WHERE table_schema = {CopyStatement.Literal(target.Schema)} AND table_name = {CopyStatement.Literal(target.TableName)}",
            returnsRows: true), options.CommandTimeout, cancellationToken);

        var row = result.Rows.FirstOrDefault();
        var replicas = row is null ? null : Text(row, "number_of_replicas");
        long? refresh = row is not null && row.TryGetValue("refresh_interval", out var r) && r is not null
            ? Convert.ToInt64(r, CultureInfo.InvariantCulture)
            : null;
        return new TableSettings(string.IsNullOrEmpty(replicas) ? "0-1" : replicas, refresh);
    }

    private Task ApplyBulkSettingsAsync(ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
        => client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            $"ALTER TABLE {target.QualifiedTable} SET (number_of_replicas = 0, refresh_interval = 0)"),
            options.CommandTimeout, cancellationToken);

    private async Task RestoreSettingsAsync(
        ArchiveCopyTarget target, TableSettings previous, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var refresh = previous.RefreshIntervalMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : "1000";
        await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            $"ALTER TABLE {target.QualifiedTable} SET (number_of_replicas = {CopyStatement.Literal(previous.NumberOfReplicas)}, refresh_interval = {refresh})"),
            options.CommandTimeout, cancellationToken);
    }

    private async Task RestoreSettingsSafelyAsync(ArchiveCopyTarget target, TableSettings previous, ArchiveCopyOptions options)
    {
        try
        {
            await RestoreSettingsAsync(target, previous, options, CancellationToken.None);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            // Reported with the values to put back by hand; the load result stands on its own.
            logger.LogError(
                "Could not restore the table settings of archive {ArchiveRtId} of tenant {TenantId}; set number_of_replicas = '{Replicas}' and refresh_interval = {Refresh} by hand: {Reason}",
                target.ArchiveRtId, tenantId, previous.NumberOfReplicas, previous.RefreshIntervalMs ?? 1000, ex.Message);
        }
    }

    private async Task FinishLoadAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain($"REFRESH TABLE {target.QualifiedTable}"),
            options.CommandTimeout, cancellationToken);

        if (options.OptimizeAfterLoad)
        {
            try
            {
                await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
                    $"OPTIMIZE TABLE {target.QualifiedTable} WITH (max_num_segments = 1)"),
                    options.OptimizeTimeout, cancellationToken);
            }
            catch (Exception ex) when (IsStatementFailure(ex))
            {
                // The merge only saves space and read time; the data is complete without it.
                logger.LogWarning("The segment merge of archive {ArchiveRtId} failed: {Reason}", target.ArchiveRtId, ex.Message);
            }

            await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain($"REFRESH TABLE {target.QualifiedTable}"),
                options.CommandTimeout, cancellationToken);
        }

        var rows = await CountAsync($"SELECT COUNT(*) FROM {target.QualifiedTable}", options, cancellationToken);
        if (rows != plan.TotalRows)
        {
            logger.LogWarning("Archive {ArchiveRtId} holds {Rows} rows after the load, the dump counted {Expected}.",
                target.ArchiveRtId, rows, plan.TotalRows);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static void EnsurePlanFits(ArchiveCopyTarget target, ArchiveCopyPlan plan, bool mustBeSameArchive)
    {
        if (mustBeSameArchive && plan.ArchiveRtId != target.ArchiveRtId)
        {
            throw new ArgumentException(
                $"The plan was made for archive '{plan.ArchiveRtId}', not for '{target.ArchiveRtId}'.", nameof(plan));
        }

        if (plan.Slices.Any(s => s.Table != ArchiveCopyTable.Data))
        {
            throw new NotSupportedException(
                "Copying the generation map of a rollup archive is not implemented yet; the plan holds a generation-map slice.");
        }
    }

    private ArchiveCopySliceResult Failed(
        ArchiveCopyTarget target, ArchiveCopySlice slice, string direction, string scrubbedReason, TimeSpan elapsed)
    {
        logger.LogWarning("Archive {ArchiveRtId}, slice {Slice} ({Direction}) failed: {Reason}",
            target.ArchiveRtId, slice.Key, direction, scrubbedReason);
        return new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Failed, 0, 0, elapsed, scrubbedReason);
    }

    /// <summary>The failures of a statement that end one slice (a cancellation is not one of them).</summary>
    private static bool IsStatementFailure(Exception ex)
        => ex is StreamDataCopyStatementException or StreamDataStatementTimeoutException;

    private async Task<long> CountAsync(string sql, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(sql, returnsRows: true),
            options.CommandTimeout, cancellationToken);
        return result.Rows.Count == 0 ? 0 : Long(result.Rows[0], result.Rows[0].Keys.First());
    }

    private async Task<(long FromInclusiveMs, long ToExclusiveMs, long RowCount)?> ReadBoundsAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var sql = CrateDbStreamDataRepository.BuildExportBoundsSql(target.QualifiedTable, target.TimeColumn, null);
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(sql, returnsRows: true),
            options.CommandTimeout, cancellationToken);
        var row = result.Rows.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        var min = AsUtc(row.GetValueOrDefault("export_min"));
        var max = AsUtc(row.GetValueOrDefault("export_max"));
        var count = Long(row, "export_count");
        if (min is null || max is null || count <= 0)
        {
            return null;
        }

        // Timestamps have millisecond precision; the end is exclusive, one step behind the last row.
        return (new DateTimeOffset(min.Value).ToUnixTimeMilliseconds(),
            new DateTimeOffset(max.Value).ToUnixTimeMilliseconds() + 1, count);
    }

    private static DateTime? AsUtc(object? value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => null
    };

    private static long Long(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : 0;

    private static string Text(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static string Abbreviate(string text)
        => text.Length <= MaxErrorTextCharacters ? text : text[..MaxErrorTextCharacters] + " …";

    private static string FormatBytes(long bytes)
        => bytes >= 1L << 30 ? FormattableString.Invariant($"{bytes / (double)(1L << 30):0.0} GiB")
            : bytes >= 1L << 20 ? FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MiB")
            : FormattableString.Invariant($"{bytes} bytes");

    private sealed record TableSettings(string NumberOfReplicas, long? RefreshIntervalMs);
}
