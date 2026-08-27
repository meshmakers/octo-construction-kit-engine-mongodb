using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     <see cref="ITenantContext.DisableStreamDataAsync" /> is a verified precondition (AB#4255): it is
///     refused while any archive is still Activated and only flips the flag once nothing is live.
///     Runs on the shared system tenant of <see cref="StreamDataFlagFixture" />; every test removes the
///     archives it created and leaves the tenant flag disabled.
/// </summary>
[Collection(StreamDataFlagCollection.Name)]
public class DisableStreamDataGuardTests(StreamDataFlagFixture fixture)
{
    private const string TargetCkTypeId = "Test/MeasuringPoint";

    [Fact]
    public async Task Disable_IsRefusedWhileAnArchiveIsActivated_AndSucceedsOnceItIsDisabled()
    {
        var tenantContext = await EnableStreamDataAsync();
        var rtId = await InsertRawArchiveAsync("GuardArchive");
        var store = tenantContext.GetArchiveRuntimeStore();
        try
        {
            await store.SetStatusAsync(rtId, CkArchiveStatus.Activated);

            var refusal = await Assert.ThrowsAsync<StreamDataDisableBlockedException>(
                () => tenantContext.DisableStreamDataAsync());

            refusal.Message.Should().Contain($"tenant '{tenantContext.TenantId}'")
                .And.Contain("RawArchive 'GuardArchive' (Activated)")
                .And.Contain("rollups before their source archive");
            refusal.ActivatedArchives.Should().ContainSingle(a => a.RtId == rtId);
            (await tenantContext.IsStreamDataEnabledAsync()).Should().BeTrue("a refused disable must not touch the flag");

            await store.SetStatusAsync(rtId, CkArchiveStatus.Disabled);
            await tenantContext.DisableStreamDataAsync();

            (await tenantContext.IsStreamDataEnabledAsync()).Should().BeFalse();
        }
        finally
        {
            await store.ArchiveEntityAsync(rtId);
        }
    }

    [Fact]
    public async Task Disable_IgnoresCreatedAndFailedArchives()
    {
        var tenantContext = await EnableStreamDataAsync();
        var created = await InsertRawArchiveAsync("CreatedArchive");
        var failed = await InsertRawArchiveAsync("FailedArchive");
        var store = tenantContext.GetArchiveRuntimeStore();
        try
        {
            await store.SetStatusAsync(failed, CkArchiveStatus.Failed);

            await tenantContext.DisableStreamDataAsync();

            (await tenantContext.IsStreamDataEnabledAsync()).Should().BeFalse();
        }
        finally
        {
            await store.ArchiveEntityAsync(created);
            await store.ArchiveEntityAsync(failed);
        }
    }

    [Fact]
    public async Task Disable_ChecksTheArchives_EvenWhenTheFlagIsAlreadyDisabled()
    {
        // The invariant is about the end state: "disable succeeded" must always mean "no archive is
        // activated", also for the idempotent re-disable of a tenant that was disabled before the guard.
        var tenantContext = await EnableStreamDataAsync();
        await tenantContext.DisableStreamDataAsync();
        var rtId = await InsertRawArchiveAsync("LateArchive");
        var store = tenantContext.GetArchiveRuntimeStore();
        try
        {
            await store.SetStatusAsync(rtId, CkArchiveStatus.Activated);

            await Assert.ThrowsAsync<StreamDataDisableBlockedException>(() => tenantContext.DisableStreamDataAsync());
        }
        finally
        {
            await store.ArchiveEntityAsync(rtId);
        }
    }

    [Fact]
    public async Task Disable_SkipsTheCheck_ForATenantWithoutTheStreamDataModel()
    {
        // Without the System.StreamData model no archive entity can exist; enumerating the store would
        // throw CkCacheException, so the guard short-circuits on model presence.
        const string childTenantId = "streamflagchild";
        var systemContext = fixture.GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, childTenantId, childTenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            ITenantContext child;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                child = await systemContext.GetChildTenantContextAsync(session, childTenantId);
                await session.CommitTransactionAsync();
            }

            await child.DisableStreamDataAsync();

            (await child.IsStreamDataEnabledAsync()).Should().BeFalse();
        }
        finally
        {
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, childTenantId);
            await session.CommitTransactionAsync();
        }
    }

    private async Task<ITenantContext> EnableStreamDataAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await tenantContext.EnableStreamDataAsync();
        return tenantContext;
    }

    private async Task<OctoObjectId> InsertRawArchiveAsync(string wellKnownName)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        var archive = new RtRawArchive
        {
            RtWellKnownName = wellKnownName,
            TargetCkTypeId = TargetCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            // Columns is a mandatory attribute of Archive; one ingested column is enough.
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "CounterReading", Indexed = false, Required = false },
            },
        };

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, archive);
        await session.CommitTransactionAsync();
        return archive.RtId;
    }
}
