using FakeItEasy;

using FluentAssertions;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The tenant drop also drops the tenant's stream data namespace through
///     <see cref="IStreamDataRepositoryFactory.DeleteDatabaseAsync" /> (AB#4255) - best-effort, after the
///     database, and only when stream data is enabled at instance level.
/// </summary>
[Collection(StreamDataDropCollection.Name)]
public class DropTenantStreamDataTests(StreamDataDropFixture fixture)
{
    [Fact]
    public async Task DropChildTenant_DropsTheStreamDataNamespace_OfThatTenant()
    {
        const string tenantId = "streamdropchild";
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);
        await CreateChildAsync(tenantId);

        await DropChildAsync(tenantId);

        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteDatabaseAsync(
                A<string>.That.Matches(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase))))
            .MustHaveHappenedOnceExactly();
        (await IsChildExistingAsync(tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task DropChildTenant_Succeeds_WhenTheNamespaceDropFails()
    {
        const string tenantId = "streamdropfailing";
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);
        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteDatabaseAsync(
                A<string>.That.Matches(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase))))
            .Throws(new InvalidOperationException("CrateDB unreachable"));
        await CreateChildAsync(tenantId);

        // Best-effort: the tenant is already deleted, the failure is logged, the drop completes.
        await DropChildAsync(tenantId);

        (await IsChildExistingAsync(tenantId)).Should().BeFalse();
    }

    private async Task CreateChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DropChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.DropChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<bool> IsChildExistingAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var existing = await systemContext.IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return existing;
    }
}

/// <summary>With <c>StreamData:Enabled = false</c> the registered factory is never asked to drop anything.</summary>
[Collection(StreamDataDisabledDropCollection.Name)]
public class DropTenantStreamDataDisabledInstanceTests(StreamDataDisabledDropFixture fixture)
{
    [Fact]
    public async Task DropChildTenant_DoesNotTouchStreamData_WhenDisabledAtInstanceLevel()
    {
        const string tenantId = "streamdropoff";
        var systemContext = fixture.GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }

        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteDatabaseAsync(A<string>._)).MustNotHaveHappened();
    }
}
