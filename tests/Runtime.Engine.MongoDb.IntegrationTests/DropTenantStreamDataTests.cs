using FakeItEasy;

using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Dropping a tenant for good (<c>dropStreamData: true</c> - the tenant delete and Clear) also drops
///     the stream data tables of exactly the tenant's own archives through
///     <see cref="IStreamDataRepositoryFactory.DeleteArchiveTablesAsync" /> (AB#4255): best-effort, after
///     the database, only when stream data is enabled at instance level. A plain database drop (the
///     default - restore over an existing tenant, create-rollback) never touches them.
/// </summary>
[Collection(StreamDataDropCollection.Name)]
public class DropTenantStreamDataTests(StreamDataDropFixture fixture)
{
    [Fact]
    public async Task DropChildTenant_ForGood_DropsTheTablesOfEveryArchiveOfThatTenant()
    {
        const string tenantId = "streamdropchild";
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);
        var (created, disabled) = await CreateChildWithArchivesAsync(tenantId);

        await DropChildAsync(tenantId, dropStreamData: true);

        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteArchiveTablesAsync(
                A<string>.That.Matches(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase)),
                A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Count == 2 && ids.Contains(created) && ids.Contains(disabled))))
            // All statuses are dropped - a Created archive's DROP IF EXISTS is harmless.
            .MustHaveHappenedOnceExactly();
        (await IsChildExistingAsync(tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task DropChildTenant_ForADatabaseSwap_LeavesTheTablesAlone()
    {
        // A restore over an existing tenant or a create-rollback drops the database but not the tenant:
        // the same archives exist afterwards and must find their tables again (the reviewer's finding
        // on RestoreRepositoryJob - a Mongo-only restore used to lose all stream data).
        const string tenantId = "streamdropswap";
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);
        await CreateChildWithArchivesAsync(tenantId);

        await DropChildAsync(tenantId, dropStreamData: false);

        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteArchiveTablesAsync(A<string>._, A<IReadOnlyList<OctoObjectId>>._))
            .MustNotHaveHappened();
        (await IsChildExistingAsync(tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task DropChildTenant_Succeeds_WhenTheTableDropFails()
    {
        const string tenantId = "streamdropfailing";
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);
        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteArchiveTablesAsync(
                A<string>.That.Matches(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase)),
                A<IReadOnlyList<OctoObjectId>>._))
            .Throws(new InvalidOperationException("CrateDB unreachable"));
        await CreateChildWithArchivesAsync(tenantId);

        // Best-effort: the tenant is already deleted, the failure is logged, the drop completes.
        await DropChildAsync(tenantId, dropStreamData: true);

        (await IsChildExistingAsync(tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task DropChildTenant_DoesNotAskTheBackend_WhenTheTenantHasNoArchives()
    {
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);

        // Stream data enabled (model imported) but no archive entity.
        const string enabledWithoutArchives = "streamdropempty";
        await CreateChildAsync(enabledWithoutArchives);
        await (await GetChildAsync(enabledWithoutArchives)).EnableStreamDataAsync();
        await DropChildAsync(enabledWithoutArchives, dropStreamData: true);

        // Never opted into stream data (no model) - the archive store cannot even be enumerated.
        const string withoutModel = "streamdropnomodel";
        await CreateChildAsync(withoutModel);
        await DropChildAsync(withoutModel, dropStreamData: true);

        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteArchiveTablesAsync(A<string>._, A<IReadOnlyList<OctoObjectId>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ClearChildTenant_DropsTheTablesOfItsArchives()
    {
        // Clear empties the tenant: the archive entities go with the database, keeping their tables
        // would only orphan them.
        const string tenantId = "streamdropclear";
        Fake.ClearRecordedCalls(fixture.StreamDataRepositoryFactory);
        var (created, disabled) = await CreateChildWithArchivesAsync(tenantId);

        try
        {
            var systemContext = fixture.GetSystemContext();
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.ClearChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteArchiveTablesAsync(
                    A<string>.That.Matches(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase)),
                    A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Count == 2 && ids.Contains(created) && ids.Contains(disabled))))
                .MustHaveHappenedOnceExactly();
            (await IsChildExistingAsync(tenantId)).Should().BeTrue("Clear re-creates the tenant");
        }
        finally
        {
            await DropChildAsync(tenantId, dropStreamData: true);
        }
    }

    /// <summary>Creates a child tenant with stream data enabled and two raw archives (Created and Disabled).</summary>
    private async Task<(OctoObjectId Created, OctoObjectId Disabled)> CreateChildWithArchivesAsync(string tenantId)
    {
        await CreateChildAsync(tenantId);
        var child = await GetChildAsync(tenantId);
        await child.EnableStreamDataAsync();

        var created = await InsertRawArchiveAsync(child, "CreatedArchive");
        var disabled = await InsertRawArchiveAsync(child, "DisabledArchive");
        await child.GetArchiveRuntimeStore().SetStatusAsync(disabled, CkArchiveStatus.Disabled);
        return (created, disabled);
    }

    private static async Task<OctoObjectId> InsertRawArchiveAsync(ITenantContext child, string wellKnownName)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = wellKnownName,
            TargetCkTypeId = "Test/MeasuringPoint",
            Status = RtCkArchiveStatusEnum.Created,
            // Columns is a mandatory attribute of Archive; one ingested column is enough.
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "CounterReading", Indexed = false, Required = false },
            },
        };

        var repository = child.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, archive);
        await session.CommitTransactionAsync();
        return archive.RtId;
    }

    private async Task CreateChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<ITenantContext> GetChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var child = await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return child;
    }

    private async Task DropChildAsync(string tenantId, bool dropStreamData)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.DropChildTenantAsync(session, tenantId, dropStreamData);
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
            await systemContext.DropChildTenantAsync(session, tenantId, dropStreamData: true);
            await session.CommitTransactionAsync();
        }

        A.CallTo(() => fixture.StreamDataRepositoryFactory.DeleteArchiveTablesAsync(A<string>._, A<IReadOnlyList<OctoObjectId>>._))
            .MustNotHaveHappened();
    }
}
