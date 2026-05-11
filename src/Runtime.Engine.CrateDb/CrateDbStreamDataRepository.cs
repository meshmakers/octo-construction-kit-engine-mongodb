using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// CrateDB implementation of <see cref="IStreamDataRepository"/>.
/// Encapsulates query orchestration, field resolution, pagination, and result transformation.
/// </summary>
internal class CrateDbStreamDataRepository : IStreamDataRepository
{
    private readonly ILogger<CrateDbStreamDataRepository> _logger;
    private readonly ICkCacheService _ckCacheService;
    private readonly IStreamDataDatabaseClient _databaseClient;
    private readonly IStreamDataDatabaseManagementClient _managementClient;
    private readonly ICkArchiveRuntimeStore _archiveStore;
    private readonly StreamDataConfiguration _configuration;
    private readonly string _tenantId;

    public CrateDbStreamDataRepository(
        ILogger<CrateDbStreamDataRepository> logger,
        ICkCacheService ckCacheService,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IOptions<StreamDataConfiguration> configuration,
        string tenantId,
        ICkArchiveRuntimeStore archiveStore)
    {
        _logger = logger;
        _ckCacheService = ckCacheService;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _archiveStore = archiveStore;
        _configuration = configuration.Value;
        _tenantId = tenantId;
    }

    public Task EnsureDatabaseCreatedAsync()
    {
        // After T17 there is no per-tenant control plane table — schemas are created implicitly
        // by CrateDB the first time an archive table inside them is provisioned. Keep the method
        // as a no-op so the callsite that opts a tenant into stream data still has a hook.
        return Task.CompletedTask;
    }

    public Task DeleteDatabaseAsync()
    {
        return _managementClient.DeleteStreamDataDatabaseAsync(_tenantId);
    }

    public async Task EnsureArchiveCreatedAsync(CkArchiveSnapshot snapshot)
    {
        // The tenant schema is implicit in the qualified table identifier — CrateDB creates the
        // schema on first table inside it, so no separate `CREATE SCHEMA` step is needed.
        var resolvedColumns = ArchivePathTypeResolver.Resolve(
            _ckCacheService, _tenantId, snapshot.TargetCkTypeId, snapshot.Columns);

        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, snapshot.RtId.ToString());
        var sql = ArchiveDdlGenerator.GenerateCreateTable(
            qualifiedTable, resolvedColumns, _configuration.NumberOfShards, _configuration.NumberOfReplicas);

        _logger.LogDebug(
            "Provisioning archive table {Table} with {ColumnCount} user columns for tenant {TenantId}",
            qualifiedTable, resolvedColumns.Count, _tenantId);
        await _managementClient.ExecuteDdlAsync(_tenantId, sql);
    }

    /// <inheritdoc />
    public async Task DeleteArchiveAsync(OctoObjectId archiveRtId)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var sql = ArchiveDdlGenerator.GenerateDropTable(qualifiedTable);
        _logger.LogDebug("Dropping archive table {Table} for tenant {TenantId}", qualifiedTable, _tenantId);
        await _managementClient.ExecuteDdlAsync(_tenantId, sql);
    }

    public async Task InsertAsync(OctoObjectId archiveRtId, StreamDataPoint datapoint)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);

        // See the bulk overload for the rationale; per-archive tables only accept rows of the
        // archive's own TargetCkTypeId.
        if (datapoint.CkTypeId != snapshot.TargetCkTypeId)
        {
            _logger.LogDebug(
                "Archive {ArchiveRtId} (target {TargetCkTypeId}): skipped 1 datapoint with mismatched CkTypeId {ActualCkTypeId}",
                archiveRtId, snapshot.TargetCkTypeId, datapoint.CkTypeId);
            return;
        }

        using var activity = CrateDbDiagnostics.ActivitySource.StartActivity("crate.insert");
        activity?.SetTag("streamdata.tenant", _tenantId);
        activity?.SetTag("streamdata.archive.rtid", archiveRtId.ToString());

        var (qualifiedTable, userColumnNames) = ResolveTableAndColumns(snapshot, archiveRtId);

        var sw = Stopwatch.StartNew();
        var dto = MapToDataPointDto(datapoint);
        await _databaseClient.InsertDataAsync(_tenantId, qualifiedTable, userColumnNames, dto);
        sw.Stop();

        CrateDbDiagnostics.InsertDurationMs.Record(sw.Elapsed.TotalMilliseconds,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()),
            new("batch_size_bucket", CrateDbDiagnostics.BatchSizeBucket(1)));
        CrateDbDiagnostics.InsertedPoints.Add(1,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()));
    }

    public async Task InsertAsync(OctoObjectId archiveRtId, IEnumerable<StreamDataPoint> datapoints)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);

        // Materialise once so we can both count and pass to the client.
        var materialised = datapoints as IReadOnlyList<StreamDataPoint> ?? datapoints.ToList();

        // Filter to items whose CkTypeId matches the archive's TargetCkTypeId. An archive is
        // schema-bound to one CkType: its user columns come from that type's attribute graph and
        // none of the others. Heterogeneous callers (e.g. the Loxone pipeline that flattens both
        // Control updates and EnergyIQ/Space mapping updates into a single _updateItems list)
        // would otherwise push rows of the wrong type into the table — semantically meaningless
        // and a frequent root cause of CrateDB connection-state pollution after a failed insert.
        var filtered = materialised.Where(p => p.CkTypeId == snapshot.TargetCkTypeId).ToList();
        var skipped = materialised.Count - filtered.Count;
        if (skipped > 0)
        {
            _logger.LogDebug(
                "Archive {ArchiveRtId} (target {TargetCkTypeId}): skipped {Skipped} of {Total} datapoints with mismatched CkTypeId",
                archiveRtId, snapshot.TargetCkTypeId, skipped, materialised.Count);
        }
        if (filtered.Count == 0)
        {
            return;
        }

        using var activity = CrateDbDiagnostics.ActivitySource.StartActivity("crate.insert");
        activity?.SetTag("streamdata.tenant", _tenantId);
        activity?.SetTag("streamdata.archive.rtid", archiveRtId.ToString());
        activity?.SetTag("streamdata.batch_size", filtered.Count);

        var (qualifiedTable, userColumnNames) = ResolveTableAndColumns(snapshot, archiveRtId);

        var sw = Stopwatch.StartNew();
        var dtos = filtered.Select(MapToDataPointDto);
        await _databaseClient.InsertDataAsync(_tenantId, qualifiedTable, userColumnNames, dtos);
        sw.Stop();

        var bucket = CrateDbDiagnostics.BatchSizeBucket(filtered.Count);
        CrateDbDiagnostics.InsertDurationMs.Record(sw.Elapsed.TotalMilliseconds,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()),
            new("batch_size_bucket", bucket));
        CrateDbDiagnostics.InsertedPoints.Add(filtered.Count,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()));
    }

    /// <summary>
    /// Computes the qualified per-archive table name and the camelCase user-column list from the
    /// archive snapshot.
    /// </summary>
    private (string qualifiedTable, IReadOnlyList<string> userColumnNames) ResolveTableAndColumns(
        CkArchiveSnapshot snapshot, OctoObjectId archiveRtId)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var userColumnNames = snapshot.Columns.Select(c => ColumnNameMapper.PathToColumnName(c.Path)).ToList();
        return (qualifiedTable, userColumnNames);
    }

    public async Task<StreamDataQueryResult> ExecuteQueryAsync(OctoObjectId archiveRtId, StreamDataQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        var fieldResolver = CreateFieldResolver(snapshot);

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        q.IncludeDefaultVariables();
        q.WithCkTypeIdFilter(options.CkTypeId);

        // Resolve and add columns
        var resolvedColumnNames = ResolveAndAddColumns(q, fieldResolver, options.Columns);

        // Time filter
        if (options.From is not null && options.To is not null)
        {
            q.WithTimeFilter(options.From.Value, options.To.Value);
        }

        // RtId scope filter
        AddRtIdFilter(q, options.RtIds);

        // Sort orders
        AddSortOrders(q, fieldResolver, options.SortOrders);

        // Field filters
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        // Execute with pagination
        var (data, totalCount, effectiveOffset) = await ExecutePaginatedQueryAsync(
            q, options.Offset, options.PageSize, options.Limit);

        var rows = data.Select(dp => MapToStreamDataRow(dp, resolvedColumnNames)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<StreamDataQueryResult> ExecuteAggregationQueryAsync(
        OctoObjectId archiveRtId, StreamDataAggregationQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        var fieldResolver = CreateFieldResolver(snapshot);

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        q.WithCkTypeIdFilter(options.CkTypeId);

        // Add aggregation columns. SQL aliases need to be unique (to support e.g. AVG+MAX
        // of the same attribute), so we use "{func}_{alias}". The output column name is
        // the original attribute path — we remap via outputNameBySqlAlias when building rows.
        var outputColumnNames = new List<string>();
        var outputNameBySqlAlias = new Dictionary<string, string>();

        foreach (var col in options.AggregationColumns)
        {
            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            var sqlAlias = $"{aggFunc}_{resolved.CrateDbName}";
            q.AddAggregationVariable(resolved.CrateDbName, aggFunc, sqlAlias);

            outputColumnNames.Add(resolved.CrateDbName);
            outputNameBySqlAlias[sqlAlias] = resolved.CrateDbName;
        }

        // Time filter
        if (options.From is not null && options.To is not null)
        {
            q.WithTimeFilter(options.From.Value, options.To.Value);
        }

        AddRtIdFilter(q, options.RtIds);
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(q);
        _logger.LogDebug("Executing aggregation SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);

        var rows = data.Select(dp => MapAggregationRow(dp, outputColumnNames, outputNameBySqlAlias)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    public async Task<StreamDataQueryResult> ExecuteGroupedAggregationQueryAsync(
        OctoObjectId archiveRtId, StreamDataGroupedAggregationQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        var fieldResolver = CreateFieldResolver(snapshot);

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        q.WithCkTypeIdFilter(options.CkTypeId);

        // Group-by columns as non-aggregation variables. The CrateQueryCompiler automatically
        // groups by all non-aggregation variables when aggregation variables are present.
        var outputColumnNames = new List<string>();
        var outputNameBySqlAlias = new Dictionary<string, string>();

        foreach (var groupCol in options.GroupByColumns)
        {
            var resolved = fieldResolver.Resolve(groupCol);
            if (resolved == null) continue;

            q.AddVariable(resolved.CrateDbName, resolved.CrateDbName, null);
            outputColumnNames.Add(resolved.CrateDbName);
            // Grouping columns use CrateDbName as SQL alias — identity mapping
            outputNameBySqlAlias[resolved.CrateDbName] = resolved.CrateDbName;
        }

        // Aggregation columns with unique SQL aliases
        foreach (var col in options.AggregationColumns)
        {
            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            var sqlAlias = $"{aggFunc}_{resolved.CrateDbName}";
            q.AddAggregationVariable(resolved.CrateDbName, aggFunc, sqlAlias);

            outputColumnNames.Add(resolved.CrateDbName);
            outputNameBySqlAlias[sqlAlias] = resolved.CrateDbName;
        }

        if (options.From is not null && options.To is not null)
        {
            q.WithTimeFilter(options.From.Value, options.To.Value);
        }

        AddRtIdFilter(q, options.RtIds);
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(q);
        _logger.LogDebug("Executing grouped aggregation SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);

        var rows = data.Select(dp => MapAggregationRow(dp, outputColumnNames, outputNameBySqlAlias)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    public async Task<StreamDataQueryResult> ExecuteDownsamplingQueryAsync(
        OctoObjectId archiveRtId, StreamDataDownsamplingQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        if (options.From is null || options.To is null || options.Limit is null)
        {
            throw StreamDataException.InvalidQueryParameters(
                "Downsampling queries require From, To, and Limit (bucket count).");
        }

        var fieldResolver = CreateFieldResolver(snapshot);

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        q.WithCkTypeIdFilter(options.CkTypeId);
        q.WithDownsampling(options.Limit.Value, options.From.Value, options.To.Value);

        // Timestamp is always first in the output for downsampling (the bin start time).
        // It maps from the "T" alias set by the downsampling SQL generator.
        q.AddVariable(Constants.Timestamp, "T", null);

        var outputColumnNames = new List<string> { Constants.Timestamp };
        var outputNameBySqlAlias = new Dictionary<string, string>();

        foreach (var col in options.AggregationColumns)
        {
            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            var sqlAlias = $"{aggFunc}_{resolved.CrateDbName}";
            q.AddAggregationVariable(resolved.CrateDbName, aggFunc, sqlAlias);

            outputColumnNames.Add(resolved.CrateDbName);
            outputNameBySqlAlias[sqlAlias] = resolved.CrateDbName;
        }

        AddRtIdFilter(q, options.RtIds);
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(q);
        _logger.LogDebug("Executing downsampling SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);

        // Detect empty bins via __binCount (COUNT(d."Timestamp") from generate_series LEFT JOIN).
        // For empty bins, force aggregation values to null regardless of what CrateDB returned.
        const string binCountKey = "__binCount";
        var rows = new List<StreamDataRow>();
        foreach (var dp in data)
        {
            var isEmptyBin =
                dp.Attributes?.TryGetValue(binCountKey, out var binCountObj) == true &&
                binCountObj != null &&
                Convert.ToInt64(binCountObj) == 0;

            var values = new Dictionary<string, object?>();
            foreach (var col in outputColumnNames)
            {
                if (col == Constants.Timestamp)
                {
                    values[col] = dp.Timestamp;
                    continue;
                }

                if (isEmptyBin)
                {
                    values[col] = null;
                    continue;
                }

                // Find the SQL alias that maps to this output column name
                var sqlAlias = outputNameBySqlAlias
                    .FirstOrDefault(kvp => kvp.Value == col).Key;
                object? value = null;
                if (sqlAlias != null)
                {
                    dp.Attributes?.TryGetValue(sqlAlias, out value);
                }
                else
                {
                    dp.Attributes?.TryGetValue(col, out value);
                }

                values[col] = value;
            }

            rows.Add(new StreamDataRow
            {
                Timestamp = dp.Timestamp,
                RtId = dp.RtId,
                CkTypeId = dp.CkTypeId,
                RtCreationDateTime = dp.RtCreationDateTime,
                RtChangedDateTime = dp.RtChangedDateTime,
                Values = values
            });
        }

        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    /// <inheritdoc />
    public async Task<int> AggregateBucketAsync(
        CkArchiveSnapshot sourceArchive,
        CkRollupArchiveSnapshot rollup,
        DateTime bucketStart,
        DateTime bucketEnd,
        CancellationToken cancellationToken)
    {
        var sourceTable = TenantSchema.QualifiedArchiveTable(_tenantId, sourceArchive.RtId.ToString());
        var targetTable = TenantSchema.QualifiedArchiveTable(_tenantId, rollup.RtId.ToString());

        var sql = RollupAggregationSqlBuilder.Build(
            sourceTable,
            targetTable,
            rollup.TargetCkTypeId.FullName,
            rollup.Aggregations,
            bucketStart,
            bucketEnd);

        _logger.LogDebug(
            "Rollup aggregation SQL for {RollupRtId} bucket [{BucketStart:O}, {BucketEnd:O}): {Sql}",
            rollup.RtId, bucketStart, bucketEnd, sql);

        var affected = await _databaseClient.ExecuteNonQueryAsync(_tenantId, sql, cancellationToken);

        CrateDbDiagnostics.RollupBucketUpserts.Add(affected,
            new("tenant", _tenantId),
            new("rollup", rollup.RtId.ToString()));

        return affected;
    }

    #region Private helpers

    /// <summary>
    /// Verifies that the archive identified by <paramref name="archiveRtId"/> exists and is in
    /// status <see cref="CkArchiveStatus.Activated"/>; throws otherwise.
    /// </summary>
    private async Task<CkArchiveSnapshot> EnsureArchiveActivatedAsync(OctoObjectId archiveRtId)
    {
        var snapshot = await _archiveStore.GetAsync(archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        if (snapshot.Status != CkArchiveStatus.Activated)
        {
            throw new ArchiveNotActivatedException(archiveRtId, snapshot.Status);
        }

        return snapshot;
    }

    private static StreamDataFieldResolver CreateFieldResolver(CkArchiveSnapshot snapshot)
    {
        // T17 archives are CK-type-agnostic: the queryable column set is exactly the columns the
        // archive was configured to capture.
        return new StreamDataFieldResolver(snapshot.Columns.Select(c => c.Path));
    }

    private static List<string> ResolveAndAddColumns(
        CrateQueryBuilder q,
        StreamDataFieldResolver fieldResolver,
        IReadOnlyList<string> columns)
    {
        var resolvedColumnNames = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            var resolved = fieldResolver.Resolve(column);
            if (resolved == null) continue;

            // Canonical PascalCase is what StreamDataRow.Values is keyed by.
            resolvedColumnNames.Add(resolved.CrateDbName);

            if (resolved.Category == StreamDataFieldCategory.Default)
            {
                // Default fields are already included by IncludeDefaultVariables()
                continue;
            }

            // SQL alias = CrateDbName so CrateDB returns the column already in canonical form.
            q.AddVariable(resolved.CrateDbName, resolved.CrateDbName, null);
        }

        return resolvedColumnNames;
    }

    private static void AddRtIdFilter(CrateQueryBuilder q, IReadOnlyList<OctoObjectId>? rtIds)
    {
        if (rtIds is not { Count: > 0 }) return;
        q.AddWhereIn("RtId", rtIds.Select(x => x.ToString()).ToArray());
    }

    private static void AddSortOrders(
        CrateQueryBuilder q,
        StreamDataFieldResolver fieldResolver,
        IReadOnlyList<SortOrderItem>? sortOrders)
    {
        if (sortOrders == null) return;

        foreach (var sort in sortOrders)
        {
            var resolved = fieldResolver.Resolve(sort.AttributePath);
            if (resolved == null) continue;

            // SQL aliases now use CrateDbName (PascalCase) for both default and data fields.
            var sortPath = resolved.CrateDbName;

            var sortOrder = sort.SortOrder == SortOrders.Descending
                ? SortOrderDto.Descending
                : SortOrderDto.Ascending;

            q.OrderBy(sortPath, sortOrder);
        }
    }

    private static void AddFieldFilters(
        CrateQueryBuilder q,
        StreamDataFieldResolver fieldResolver,
        IReadOnlyList<FieldFilter>? fieldFilters)
    {
        if (fieldFilters == null) return;

        foreach (var filter in fieldFilters)
        {
            // IsNull and IsNotNull have no comparison value — allow them through
            var isNullCheck = filter.Operator is FieldFilterOperator.IsNull or FieldFilterOperator.IsNotNull;
            if (!isNullCheck && filter.ComparisonValue == null) continue;

            var resolved = fieldResolver.Resolve(filter.AttributePath);
            if (resolved == null) continue;

            var op = MapFieldFilterOperator(filter.Operator);

            switch (filter.Operator)
            {
                case FieldFilterOperator.Between:
                    q.AddFieldFilter(resolved.CrateDbName, op,
                        filter.ComparisonValue?.ToString() ?? "",
                        secondaryValue: filter.SecondaryValue?.ToString());
                    break;

                case FieldFilterOperator.In:
                case FieldFilterOperator.NotIn:
                {
                    var valueList = filter.ComparisonValue switch
                    {
                        IEnumerable<string> strings => strings.ToList(),
                        IEnumerable<object> objects => objects.Select(o => o.ToString() ?? "").ToList(),
                        _ => new List<string> { filter.ComparisonValue?.ToString() ?? "" }
                    };
                    q.AddFieldFilter(resolved.CrateDbName, op, "", valueList: valueList);
                    break;
                }

                case FieldFilterOperator.IsNull:
                case FieldFilterOperator.IsNotNull:
                    q.AddFieldFilter(resolved.CrateDbName, op, "");
                    break;

                default:
                    q.AddFieldFilter(resolved.CrateDbName, op, filter.ComparisonValue!.ToString()!);
                    break;
            }
        }
    }

    private async Task<(List<DataPointDto> Data, int TotalCount, int Offset)> ExecutePaginatedQueryAsync(
        CrateQueryBuilder q,
        int? offset,
        int? pageSize,
        int? rowCap)
    {
        var compiler = new CrateQueryCompiler();

        // Compile count query BEFORE setting LIMIT/OFFSET
        var countSql = compiler.CompileCountQuery(q);

        // Add Timestamp tiebreaker for deterministic pagination
        q.AddOrderByTiebreaker(Constants.Timestamp, SortOrderDto.Ascending);

        var effectiveOffset = offset.GetValueOrDefault(0);

        // Compute effective page limit considering rowCap
        int? effectivePageLimit = pageSize;
        if (rowCap.HasValue && effectivePageLimit.HasValue)
        {
            effectivePageLimit = Math.Min(effectivePageLimit.Value, Math.Max(0, rowCap.Value - effectiveOffset));
        }
        else if (rowCap.HasValue)
        {
            effectivePageLimit = Math.Max(0, rowCap.Value - effectiveOffset);
        }

        // Edge case: offset is beyond the row cap
        if (effectivePageLimit is <= 0)
        {
            var emptyCountResult = await _databaseClient.GetCountAsync(_tenantId, countSql);
            var emptyTotalCount = rowCap.HasValue
                ? (int)Math.Min(emptyCountResult, rowCap.Value)
                : (int)emptyCountResult;
            return ([], emptyTotalCount, effectiveOffset);
        }

        if (effectiveOffset > 0) q.WithOffset(effectiveOffset);
        if (effectivePageLimit is > 0) q.WithLimit(effectivePageLimit.Value);

        var dataSql = compiler.CompileQuery(q);

        _logger.LogDebug("Executing paginated SQL: {DataSql} | Count: {CountSql}", dataSql, countSql);

        // Execute count + data in parallel
        var countTask = _databaseClient.GetCountAsync(_tenantId, countSql);
        var dataTask = _databaseClient.GetDataAsync(_tenantId, dataSql);
        await Task.WhenAll(countTask, dataTask);

        var totalCount = countTask.Result;
        var effectiveTotalCount = rowCap.HasValue
            ? (int)Math.Min(totalCount, rowCap.Value)
            : (int)totalCount;

        return (dataTask.Result, effectiveTotalCount, effectiveOffset);
    }

    private static Dtos.StreamDataFieldFilterOperator MapFieldFilterOperator(FieldFilterOperator op)
    {
        return op switch
        {
            FieldFilterOperator.Equals           => Dtos.StreamDataFieldFilterOperator.Equals,
            FieldFilterOperator.NotEquals        => Dtos.StreamDataFieldFilterOperator.NotEquals,
            FieldFilterOperator.GreaterThan      => Dtos.StreamDataFieldFilterOperator.GreaterThan,
            FieldFilterOperator.GreaterEqualThan => Dtos.StreamDataFieldFilterOperator.GreaterThanOrEqual,
            FieldFilterOperator.LessThan         => Dtos.StreamDataFieldFilterOperator.LessThan,
            FieldFilterOperator.LessEqualThan    => Dtos.StreamDataFieldFilterOperator.LessThanOrEqual,
            FieldFilterOperator.Like             => Dtos.StreamDataFieldFilterOperator.Like,
            FieldFilterOperator.In               => Dtos.StreamDataFieldFilterOperator.In,
            FieldFilterOperator.NotIn            => Dtos.StreamDataFieldFilterOperator.NotIn,
            FieldFilterOperator.IsNull           => Dtos.StreamDataFieldFilterOperator.IsNull,
            FieldFilterOperator.IsNotNull        => Dtos.StreamDataFieldFilterOperator.IsNotNull,
            FieldFilterOperator.Between          => Dtos.StreamDataFieldFilterOperator.Between,
            FieldFilterOperator.MatchRegEx or
            FieldFilterOperator.AnyEq or
            FieldFilterOperator.AnyLike or
            FieldFilterOperator.Match or
            FieldFilterOperator.Contains or
            FieldFilterOperator.StartsWith or
            FieldFilterOperator.EndsWith => throw new NotSupportedException(
                $"Operator {op} is not supported for stream data queries against CrateDB."),
            _ => throw new NotSupportedException(
                $"Operator {op} is not supported for stream data queries against CrateDB.")
        };
    }

    private static AggregationFunctionDto MapAggregationFunction(AggregationFunction func)
    {
        return func switch
        {
            AggregationFunction.Average => AggregationFunctionDto.Avg,
            AggregationFunction.Minimum => AggregationFunctionDto.Min,
            AggregationFunction.Maximum => AggregationFunctionDto.Max,
            AggregationFunction.Count   => AggregationFunctionDto.Count,
            AggregationFunction.Sum     => AggregationFunctionDto.Sum,
            _ => throw new ArgumentOutOfRangeException(nameof(func))
        };
    }

    private static DataPointDto MapToDataPointDto(StreamDataPoint point)
    {
        // Re-key attributes from raw CK paths (e.g. "sensor.reading.value") to the camelCase
        // column names that exist on the per-archive table — the data plane no longer carries a
        // dynamic `data` blob, so the dictionary must align with the table schema directly.
        var attributes = new Dictionary<string, object?>(point.Attributes.Count);
        foreach (var kvp in point.Attributes)
        {
            attributes[ColumnNameMapper.PathToColumnName(kvp.Key)] = kvp.Value;
        }
        return new DataPointDto(attributes)
        {
            RtId = point.RtId,
            CkTypeId = point.CkTypeId,
            Timestamp = point.Timestamp,
            RtWellKnownName = point.RtWellKnownName,
            RtCreationDateTime = point.RtCreationDateTime ?? default,
            RtChangedDateTime = point.RtChangedDateTime ?? default
        };
    }

    private static StreamDataRow MapToStreamDataRow(DataPointDto dp, List<string> columnNames)
    {
        var values = new Dictionary<string, object?>();
        foreach (var col in columnNames)
        {
            object? value = col switch
            {
                Constants.RtId => dp.RtId,
                Constants.CkTypeId => dp.CkTypeId,
                Constants.Timestamp => dp.Timestamp,
                Constants.RtWellKnownName => dp.RtWellKnownName,
                Constants.RtCreationDateTime => dp.RtCreationDateTime,
                Constants.RtChangedDateTime => dp.RtChangedDateTime,
                _ => dp.Attributes?.TryGetValue(col, out var v) == true ? v : null
            };
            values[col] = value;
        }

        return new StreamDataRow
        {
            RtId = dp.RtId,
            CkTypeId = dp.CkTypeId,
            Timestamp = dp.Timestamp,
            RtWellKnownName = dp.RtWellKnownName,
            RtCreationDateTime = dp.RtCreationDateTime,
            RtChangedDateTime = dp.RtChangedDateTime,
            Values = values
        };
    }

    /// <summary>
    /// Maps an aggregation query result row. Looks up values via the SQL alias
    /// (which may differ from the output column name when the same attribute is
    /// aggregated multiple times).
    /// </summary>
    private static StreamDataRow MapAggregationRow(
        DataPointDto dp,
        List<string> outputColumnNames,
        Dictionary<string, string> outputNameBySqlAlias)
    {
        var values = new Dictionary<string, object?>();

        // Always surface the SQL alias as a key too, so consumers that need to distinguish
        // multiple aggregations on the same attribute (e.g. the .Aggregations sub-connection
        // requesting Count + Min + Max + Avg + Sum on one path) can look up by
        // "Avg_voltage", "Min_voltage" etc.
        foreach (var kvp in outputNameBySqlAlias)
        {
            if (dp.Attributes != null && dp.Attributes.TryGetValue(kvp.Key, out var sqlAliasValue))
            {
                values[kvp.Key] = sqlAliasValue;
            }
        }

        // Also surface under the friendly output name (attribute path) for the .Rows
        // consumer contract. When multiple stats target the same path the last one wins;
        // consumers needing all stats should look up by SQL alias instead.
        foreach (var outputName in outputColumnNames)
        {
            var sqlAlias = outputNameBySqlAlias
                .FirstOrDefault(kvp => kvp.Value == outputName).Key;

            object? value = null;
            if (sqlAlias != null)
            {
                dp.Attributes?.TryGetValue(sqlAlias, out value);
            }
            else
            {
                dp.Attributes?.TryGetValue(outputName, out value);
            }

            values[outputName] = value;
        }

        return new StreamDataRow
        {
            RtId = dp.RtId,
            CkTypeId = dp.CkTypeId,
            Timestamp = dp.Timestamp,
            RtWellKnownName = dp.RtWellKnownName,
            RtCreationDateTime = dp.RtCreationDateTime,
            RtChangedDateTime = dp.RtChangedDateTime,
            Values = values
        };
    }

    #endregion
}
