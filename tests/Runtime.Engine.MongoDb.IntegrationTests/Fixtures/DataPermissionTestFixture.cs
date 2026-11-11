using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Resolver stub with a mutable policy table: tests set <see cref="Table" /> to activate
///     enforcement and reset it to <see cref="RtDataPolicyTable.Empty" /> afterwards. The loader
///     (System.Identity entity reads) is covered elsewhere; here the enforcement pipeline itself is
///     under test (AB#4973).
/// </summary>
public sealed class TestDataPermissionResolver : IDataPermissionResolver
{
    public RtDataPolicyTable Table { get; set; } = RtDataPolicyTable.Empty;

    public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository)
    {
        return Task.FromResult(Table);
    }

    public void Invalidate(string tenantId)
    {
    }
}

/// <summary>
///     Test CK model fixture with a swappable data-permission resolver so the read/write enforcement
///     (AB#4973) can be tested end-to-end against real MongoDB without importing the System.Identity
///     model.
/// </summary>
public class DataPermissionTestFixture : ImportTestCkModelFixture
{
    public DataPermissionTestFixture()
    {
        // Last registration wins over the TryAddSingleton in AddRuntimeEngine.
        Services.AddSingleton<IDataPermissionResolver>(Resolver);
        // AB#7279: the host sink the engine's AggregatingAuditEventSink wraps.
        Services.AddSingleton<IAuditEventSink>(AuditSink);
    }

    public TestDataPermissionResolver Resolver { get; } = new();

    public CapturingAuditEventSink AuditSink { get; } = new();
}

/// <summary>Records every audit event that reaches the host sink.</summary>
public sealed class CapturingAuditEventSink : IAuditEventSink
{
    private readonly List<AuditEvent> _events = [];

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        lock (_events)
        {
            _events.Add(auditEvent);
        }

        return Task.CompletedTask;
    }
}
