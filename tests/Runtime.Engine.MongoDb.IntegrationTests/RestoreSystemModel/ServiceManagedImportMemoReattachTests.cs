using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RestoreSystemModel;

/// <summary>
///     The per-process memo of the service-managed auto-import is keyed by the tenant's registry record: a tenant that is
///     attached again (what a restore does) is a new record, so the owning service checks it again on its next resolve -
///     also when no tenant lifecycle notification reached the process and cleared the memo (the consumer of those
///     notifications does not run here, as in a process that missed them).
/// </summary>
[Collection(ServiceManagedDescriptorCollection.Name)]
public class ServiceManagedImportMemoReattachTests(ServiceManagedDescriptorFixture fixture)
{
    private static readonly CkModelId Descriptor = ServiceManagedDescriptorFixture.DescriptorModelId;

    [Fact]
    public async Task ReattachedTenant_IsCheckedAgainOnItsNextResolve_WithoutAMemoReset()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"memo{Guid.NewGuid():N}"[..20];
        var otherDatabase = $"memodb{Guid.NewGuid():N}"[..20];
        try
        {
            // First resolve: imports the descriptor's model and arms the memo for this tenant.
            await CreateAsync(systemContext, tenantId, tenantId);
            Assert.True(await (await ResolveAsync(systemContext, tenantId)).IsCkModelExistingAsync(Descriptor));

            // A database that never saw the descriptor's model (created as a second tenant, never resolved, detached).
            await CreateAsync(systemContext, otherDatabase, otherDatabase);
            await DetachAsync(systemContext, otherDatabase);
            var otherDataSource = ((TenantContext)systemContext).CreateAdminDataSourceForRestore(otherDatabase,
                otherDatabase);
            Assert.Null(await EmbeddedCkModelImportGuard.FindInstalledAsync(otherDataSource, Descriptor.Name));

            // The tenant id is attached again, to that database: a new registry record.
            await DetachAsync(systemContext, tenantId);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, otherDatabase, tenantId);
                await session.CommitTransactionAsync();
            }

            var reattached = await ResolveAsync(systemContext, tenantId);

            Assert.Equal(otherDatabase, reattached.DatabaseName);
            Assert.True(await reattached.IsCkModelExistingAsync(Descriptor),
                "the memo of the first attachment must not hide the re-attached tenant");
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
            await ThrowawayTenant.DropAsync(systemContext, otherDatabase);
        }
    }

    private static async Task CreateAsync(ISystemContext systemContext, string databaseName, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task DetachAsync(ISystemContext systemContext, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.DetachChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private static async Task<ITenantContext> ResolveAsync(ISystemContext systemContext, string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var tenant = await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return tenant;
    }
}
