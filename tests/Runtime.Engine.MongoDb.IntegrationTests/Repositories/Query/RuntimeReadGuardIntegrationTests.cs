using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

using Microsoft.Extensions.Logging;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     AB#9824 against real MongoDB: the tenant repository reports every unbounded runtime read (no take, take above
///     the limit, <c>int.MaxValue</c>) to the read guard with the number of entities it returned - by type, by type
///     with navigation and association targets - while bounded reads and by-id reads within the limit stay silent,
///     mode Off reports nothing, a host without the opt-in has no guard, and the results are identical with and
///     without the guard.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public sealed class RuntimeReadGuardIntegrationTests(SampleRtModelDataFixture fixture) : IDisposable
{
    private readonly CapturingLogger _logger = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly RuntimeReadGuardOptions _options = new();

    public void Dispose() => _meterFactory.Dispose();

    private TenantRepository CreateRepository(bool withGuard = true)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        repository.ReadGuard = withGuard
            ? new RuntimeReadGuard(() => _options, new RuntimeReadGuardMetrics(_meterFactory), _logger,
                TimeProvider.System)
            : null;
        return repository;
    }

    private static IDisposable Node(string nodeType, int index) =>
        RuntimeReadScope.BeginNode(nodeType, nodeType, $"{index}:{nodeType}");

    private static IDisposable Execution() =>
        RuntimeReadScope.BeginExecution(new RuntimeReadExecution("t", "pipeline-1", "Read pipeline", "flow-1",
            Guid.NewGuid().ToString()));

    private List<Dictionary<string, object?>> UnboundedWarnings =>
        _logger.Warnings.Where(w => w.EventId == RuntimeReadGuardEvents.UnboundedRead).Select(w => w.State).ToList();

    [Fact]
    public async Task ByType_WithoutTakeOrAboveTheLimit_IsReported_WithTheReturnedCount_BoundedIsNot()
    {
        var repository = CreateRepository();
        using var session = await repository.GetSessionAsync();
        using var execution = Execution();

        IResultSet<RtEntity> all;
        using (Node("NoTake@1", 0))
        {
            all = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create());
        }

        using (Node("MaxValue@1", 1))
        {
            await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create(), 0, int.MaxValue);
        }

        using (Node("Bounded@1", 2))
        {
            await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create(), 0, 1000);
            await repository.GetRtEntitiesByIdAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                all.Items.Take(2).Select(e => e.RtId).ToList(), RtEntityQueryOptions.Create());
        }

        Assert.True(all.Items.Any());
        var warnings = UnboundedWarnings;
        Assert.Equal(2, warnings.Count);
        Assert.Equal("NoTake@1", warnings[0]["NodeType"]);
        Assert.Equal(RuntimeReadKinds.ByType, warnings[0]["ReadKind"]);
        Assert.Equal("null", warnings[0]["Take"]);
        Assert.Equal((long)all.Items.Count(), warnings[0]["ReturnedCount"]);
        Assert.Equal(TestCkIds.RtCkStateOrProvinceTypeId.ToString(), warnings[0]["CkTypeId"]);
        Assert.Equal("pipeline-1", warnings[0]["PipelineId"]);
        Assert.Equal("MaxValue@1", warnings[1]["NodeType"]);
        Assert.Equal("int.MaxValue", warnings[1]["Take"]);
    }

    [Fact]
    public async Task GraphByType_AndAssociationTargets_WithoutTake_AreReported()
    {
        var repository = CreateRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var navigation = RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService,
            fixture.GetSystemContext().TenantId, TestCkIds.CkDistrictTypeId, ["name"]);
        using var session = await repository.GetSessionAsync();
        using var execution = Execution();

        IResultSet<RtEntityGraphItem> graph;
        using (Node("GetQueryById@1", 0))
        {
            graph = await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
                RtEntityQueryOptions.Create(), navigation, null, null);
        }

        var states = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(), 0, 10);
        IMultipleOriginResultSet<RtEntity> targets;
        using (Node("GetAssociationTargets@1", 1))
        {
            targets = await repository.GetRtAssociationTargetsAsync(session,
                states.Items.Select(s => s.RtId).ToList(), TestCkIds.RtCkStateOrProvinceTypeId,
                SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId, GraphDirections.Inbound, null,
                RtEntityQueryOptions.Create());
        }

        var warnings = UnboundedWarnings;
        Assert.Equal(2, warnings.Count);
        Assert.Equal(RuntimeReadKinds.GraphByType, warnings[0]["ReadKind"]);
        Assert.Equal((long)graph.Items.Count(), warnings[0]["ReturnedCount"]);
        Assert.Equal(RuntimeReadKinds.AssociationTargets, warnings[1]["ReadKind"]);
        Assert.Equal(TestCkIds.RtCkDistrictTypeId.ToString(), warnings[1]["CkTypeId"]);
        Assert.Equal((long)targets.Values.Sum(r => r.Items.Count()), warnings[1]["ReturnedCount"]);
    }

    /// <summary>
    ///     An empty target id list adds no id filter (<c>AddIdFilter</c> ignores it), so an association read returns
    ///     the same targets as without a key list: it is unbounded and reported, not counted as bounded by zero keys.
    ///     A by-id read with an empty list returns nothing before it reads, so it is not reported.
    /// </summary>
    [Fact]
    public async Task EmptyKeyList_IsNoFilter_AndTheAssociationReadIsReported()
    {
        var repository = CreateRepository();
        using var session = await repository.GetSessionAsync();
        using var execution = Execution();

        IResultSet<RtEntity> byId;
        using (Node("EmptyIds@1", 0))
        {
            byId = await repository.GetRtEntitiesByIdAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                new List<OctoObjectId>(), RtEntityQueryOptions.Create());
        }

        var states = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(), 0, 10);
        var originIds = states.Items.Select(s => s.RtId).ToList();
        IMultipleOriginResultSet<RtEntity> unfiltered;
        using (RuntimeReadScope.Suppress())
        {
            unfiltered = await repository.GetRtAssociationTargetsAsync(session, originIds,
                TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());
        }

        IMultipleOriginResultSet<RtEntity> targets;
        using (Node("EmptyTargetIds@1", 1))
        {
            targets = await repository.GetRtAssociationTargetsAsync(session, originIds,
                TestCkIds.RtCkStateOrProvinceTypeId, SystemCkIds.RtCkParentChildRoleId, TestCkIds.RtCkDistrictTypeId,
                GraphDirections.Inbound, new List<OctoObjectId>(), RtEntityQueryOptions.Create());
        }

        var expected = unfiltered.Values.Sum(r => r.Items.Count());
        Assert.Empty(byId.Items);
        Assert.True(expected > 0);
        Assert.Equal(expected, targets.Values.Sum(r => r.Items.Count()));
        var warnings = UnboundedWarnings;
        Assert.Single(warnings);
        Assert.Equal("EmptyTargetIds@1", warnings[0]["NodeType"]);
        Assert.Equal(RuntimeReadKinds.AssociationTargets, warnings[0]["ReadKind"]);
        Assert.Equal((long)expected, warnings[0]["ReturnedCount"]);
    }

    [Fact]
    public void KeyCount_IsTheNumberOfIds_OnlyWhenTheListHasEntries()
    {
        Assert.Null(TenantRepository.KeyCount<OctoObjectId>(null));
        Assert.Null(TenantRepository.KeyCount(new List<OctoObjectId>()));
        Assert.Equal(2, TenantRepository.KeyCount(new List<OctoObjectId> { OctoObjectId.GenerateNewId(),
            OctoObjectId.GenerateNewId() }));
    }

    [Fact]
    public async Task ModeOff_ReportsNothing()
    {
        _options.Mode = nameof(RuntimeReadGuardMode.Off);
        var repository = CreateRepository();
        using var session = await repository.GetSessionAsync();
        using var execution = Execution();
        using var node = Node("NoTake@1", 0);

        await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());

        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task Results_AreIdentical_WithAndWithoutTheGuard()
    {
        async Task<List<string>> ReadAsync(bool withGuard)
        {
            var repository = CreateRepository(withGuard);
            using var session = await repository.GetSessionAsync();
            using var execution = Execution();
            using var node = Node("NoTake@1", 0);
            var byType = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create());
            var paged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create(), 1, 2, true);
            return byType.Items.Select(e => e.RtId.ToString())
                .Concat(["|", paged.TotalCount.ToString()])
                .Concat(paged.Items.Select(e => e.RtId.ToString()))
                .ToList();
        }

        var guarded = await ReadAsync(true);
        var unguarded = await ReadAsync(false);

        Assert.Equal(unguarded, guarded);
        Assert.NotEmpty(UnboundedWarnings);
    }

    [Fact]
    public async Task WithoutPipelineContext_TheReadIsReportedWithCallerUnknown()
    {
        var repository = CreateRepository();
        using var session = await repository.GetSessionAsync();
        Assert.Null(RuntimeReadScope.Current);

        await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());

        var warning = Assert.Single(UnboundedWarnings);
        Assert.Equal("unknown", warning["NodeType"]);
        Assert.Equal("unknown", warning["PipelineId"]);
    }

    [Fact]
    public void AHostWithoutTheOptIn_HasNoGuard()
    {
        // The fixture's host does not call AddRuntimeReadGuard (as the asset repository does not).
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        Assert.Null(repository.ReadGuard);
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Dictionary<string, object?> State);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = new();

        public List<LogEntry> Warnings
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Where(e => e.Level == LogLevel.Warning).ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var dict = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    dict[pair.Key] = pair.Value;
                }
            }

            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, eventId, dict));
            }
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly ConcurrentBag<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
        }
    }
}
