using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Configuration;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     AB#9824 through the real container of a host that calls <c>AddRuntimeReadGuard()</c>: the tenant context hands
///     the registered guard to its repositories, the lease-end hook
///     (<see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext.InvalidateTenantResolveImportGuards" />)
///     clears the guard's tenant state, and the tenant context's own registry lookups inside a node do not take the
///     node's one report.
/// </summary>
[CollectionDefinition(Name)]
public class RuntimeReadGuardWiringCollection : ICollectionFixture<RuntimeReadGuardWiringFixture>
{
    public const string Name = "RuntimeReadGuardWiring";
}

/// <summary>Test CK model fixture of a host that opted in to the runtime read guard, with a capturing logger.</summary>
public sealed class RuntimeReadGuardWiringFixture : ImportTestCkModelFixture
{
    public RuntimeReadGuardWiringFixture()
    {
        new OptInBuilder(Services).AddRuntimeReadGuard();
        Services.AddLogging(logging => logging.AddProvider(Logs));
    }

    public CapturingLoggerProvider Logs { get; } = new();

    private sealed class OptInBuilder(IServiceCollection services) : IRuntimeEngineBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IRuntimeEngineBuilder AddLocalRuntimeRepository(
            Action<LocalRuntimeRepositoryConfiguration>? setupRepositoryConfigurationAction = null) =>
            throw new NotSupportedException();
    }
}

[Collection(RuntimeReadGuardWiringCollection.Name)]
public sealed class RuntimeReadGuardWiringTests(RuntimeReadGuardWiringFixture fixture)
{
    private int UnboundedWarnings(string ckTypeId) => fixture.Logs.Entries.Count(e =>
        e.Level == LogLevel.Warning && e.Category == typeof(RuntimeReadGuard).FullName &&
        e.Message.StartsWith("Unbounded runtime-entity read", StringComparison.Ordinal) &&
        e.Message.Contains($"CK type {ckTypeId} ", StringComparison.Ordinal));

    [Fact]
    public async Task RegisteredGuard_ReachesTheRepository_AndTheLeaseEndClearsItsTenantState()
    {
        var systemContext = fixture.GetSystemContext();
        var repository = (TenantRepository)systemContext.GetTenantRepository();
        Assert.NotNull(repository.ReadGuard);

        var type = TestCkIds.RtCkStateOrProvinceTypeId;
        var before = UnboundedWarnings(type.ToString());
        using var session = await repository.GetSessionAsync();

        // Outside a pipeline: reported once per tenant, CK type and hour.
        await repository.GetRtEntitiesByTypeAsync(session, type, RtEntityQueryOptions.Create());
        await repository.GetRtEntitiesByTypeAsync(session, type, RtEntityQueryOptions.Create());
        Assert.Equal(before + 1, UnboundedWarnings(type.ToString()));

        // Lease end of the tenant: the throttle is gone, so the next read is reported again.
        systemContext.InvalidateTenantResolveImportGuards(systemContext.TenantId);
        await repository.GetRtEntitiesByTypeAsync(session, type, RtEntityQueryOptions.Create());
        Assert.Equal(before + 2, UnboundedWarnings(type.ToString()));
    }

    [Fact]
    public async Task RegistryLookupInsideANode_DoesNotHideTheNodesOwnUnboundedRead()
    {
        var systemContext = (TenantContext)fixture.GetSystemContext();
        var repository = (TenantRepository)systemContext.GetTenantRepository();
        var type = TestCkIds.RtCkCountryTypeId;
        var before = UnboundedWarnings(type.ToString());
        var tenantWarningsBefore = UnboundedWarnings("System/Tenant");

        using var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution(systemContext.TenantId,
            "pipeline-1", null, "flow-1", Guid.NewGuid().ToString()));
        using (RuntimeReadScope.BeginNode("GetRtEntitiesByType@1", "PipelineExecution/GetRtEntitiesByType@1"))
        {
            // A cross-tenant node first resolves the target tenant through the registry (filtered, no take) ...
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                Assert.Null(await systemContext.TryGetChildTenantAsync(adminSession, "ab9824-no-such-tenant"));
            }

            // ... then makes its own unbounded read, which is the one reported.
            using var session = await repository.GetSessionAsync();
            await repository.GetRtEntitiesByTypeAsync(session, type, RtEntityQueryOptions.Create());
        }

        Assert.Equal(before + 1, UnboundedWarnings(type.ToString()));
        Assert.Equal(tenantWarningsBefore, UnboundedWarnings("System/Tenant"));
    }
}
