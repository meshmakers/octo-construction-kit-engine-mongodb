using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// End-to-end tests for IBlueprintService against a real MongoDB tenant.
/// Verifies the Phase 1 / 2 / 2c behaviour:
/// - Initial apply imports seed entities and tags them with rtBlueprintSource /
///   rtBlueprintLocked / rtBlueprintAppliedAt
/// - --force re-apply records ApplicationMode = ReApply
/// - PreviewUpdate produces real counts and surfaces unlocked entities as
///   UserModified conflicts
/// - ApplyUpdate honours Safe / Merge / Full semantics
/// - Rollback restores a backed-up state
/// </summary>
[Collection("Sequential")]
public class BlueprintServiceIntegrationTests(BlueprintServiceFixture fixture)
    : IClassFixture<BlueprintServiceFixture>
{
    private readonly BlueprintServiceFixture _fixture = fixture;

    private static readonly BlueprintId TestBpV1 = new("TestBp", "1.0.0");
    private static readonly BlueprintId TestBpV2 = new("TestBp", "2.0.0");
    private static readonly RtCkId<CkTypeId> CustomerCkType = new("Test/Customer");

    /// <summary>
    /// Diagnostic to pin down WHY the Mongo provider returns null for a fresh
    /// child tenant. Not a real assertion target — flag it Skip once we know.
    /// </summary>
    [Fact]
    public async Task Diagnostic_FreshChildTenant_ResolvesViaProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var systemContext = _fixture.GetSystemContext();
        var tenantId = await _fixture.CreateTestTenantAsync("diag");

        try
        {
            var childContext = await systemContext.TryFindTenantContextAsync(tenantId);
            childContext.Should().NotBeNull(
                "TryFindTenantContextAsync should find the just-created child tenant");

            var provider = _fixture.GetRuntimeRepositoryProvider();

            // Reflect into the provider to fetch its captured _systemContext and
            // compare with the one the test used to create the tenant.
            var fieldInfo = provider.GetType().GetField("_systemContext",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var providerSystemContext = fieldInfo?.GetValue(provider);

            ReferenceEquals(providerSystemContext, systemContext).Should().BeTrue(
                $"the provider's _systemContext (hash={providerSystemContext?.GetHashCode()}) must be the same instance as the test's systemContext (hash={systemContext.GetHashCode()})");

            // Direct call against the same systemContext the test used:
            var directRepo = await systemContext.TryFindTenantRepositoryAsync(tenantId);
            directRepo.Should().NotBeNull(
                "calling TryFindTenantRepositoryAsync directly on the test's systemContext should also succeed");

            var providerRepo = await provider.GetRepositoryAsync(tenantId, ct);
            providerRepo.Should().NotBeNull(
                "IRuntimeRepositoryProvider.GetRepositoryAsync should resolve the same tenant");

            providerRepo!.TenantId.Should().Be(tenantId);
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyBlueprint_FirstTime_TagsEntitiesAndRecordsHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var history = _fixture.GetBlueprintHistory();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-first");

        try
        {
            var result = await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var allMessages = string.Join("; ",
                result.OperationResult.Messages.Select(m => $"[{m.MessageLevel}] {m.MessageText}"));

            if (!result.IsSuccess)
            {
                throw new Xunit.Sdk.XunitException($"Apply failed: {allMessages}");
            }

            result.AppliedSeedDataFiles.Should().HaveCount(1,
                $"messages: {allMessages}");
            result.EntitiesCreated.Should().Be(2, "TestBp-1.0.0 has two customers");

            var current = await history.GetCurrentAsync(tenantId, ct);
            current.Should().NotBeNull();
            current!.BlueprintId.Should().Be(TestBpV1);
            current.ApplicationMode.Should().Be(BlueprintApplicationMode.Initial);

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().HaveCount(2);

            foreach (var customer in customers)
            {
                customer.GetAttributeStringValueOrDefault("RtBlueprintSource")
                    .Should().Be(TestBpV1.FullName, "every seed entity must carry the source tag");
                customer.GetAttributeValueOrDefault<bool>("RtBlueprintLocked")
                    .Should().BeTrue("default lock state is true so updates can flow through");
                customer.GetAttributeValueOrDefault<DateTime>("RtBlueprintAppliedAt")
                    .Should().NotBeNull("applied-at must be stamped");
            }
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyBlueprint_WithForce_RecordsReApplyMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var history = _fixture.GetBlueprintHistory();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-force");

        try
        {
            (await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct))
                .IsSuccess.Should().BeTrue();

            (await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: true, ct))
                .IsSuccess.Should().BeTrue();

            var entries = await history.GetHistoryAsync(tenantId, ct);
            entries.Should().HaveCount(2);
            entries[0].ApplicationMode.Should().Be(BlueprintApplicationMode.ReApply, "second apply with --force");
            entries[1].ApplicationMode.Should().Be(BlueprintApplicationMode.Initial, "first apply");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task PreviewUpdate_MergeMode_ReportsAddedAndUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("preview-merge");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var preview = await blueprintService.PreviewUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge, ct);

            // TestBp-2.0.0 vs TestBp-1.0.0: Alpha updated, Beta dropped (not deleted in Merge), Gamma added
            preview.EntitiesToAdd.Should().Be(1, "Gamma is new");
            preview.EntitiesToUpdate.Should().Be(1, "Alpha exists, is locked, will update");
            preview.EntitiesToDelete.Should().Be(0, "Merge mode never deletes");
            preview.Conflicts.Should().BeEmpty("all current entities are locked by default");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task PreviewUpdate_FullMode_FlagsOrphanForDeletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("preview-full");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var preview = await blueprintService.PreviewUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Full, ct);

            preview.EntitiesToAdd.Should().Be(1, "Gamma is new");
            preview.EntitiesToUpdate.Should().Be(1, "Alpha will be updated");
            preview.EntitiesToDelete.Should().Be(1, "Beta is no longer in the seed and is locked");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task PreviewUpdate_UnlockedEntity_RaisesUserModifiedConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("preview-unlocked");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            // Simulate a user editing Alpha: unlock it.
            await UnlockCustomerAsync(tenantId, "Alpha");

            var preview = await blueprintService.PreviewUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge, ct);

            preview.EntitiesToUpdate.Should().Be(0, "Alpha is now unlocked, the update is skipped");
            preview.EntitiesToAdd.Should().Be(1, "Gamma is still new");
            preview.Conflicts.Should().ContainSingle(c =>
                c.ConflictType == ConflictType.UserModified
                && c.EntityWellKnownName == "Alpha");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_SafeMode_AddsNewEntitiesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-safe");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Safe, null, ct);

            result.Success.Should().BeTrue();
            result.EntitiesAdded.Should().Be(1, "Safe mode only adds Gamma");
            result.EntitiesUpdated.Should().Be(0, "Safe mode never touches existing");
            result.EntitiesDeleted.Should().Be(0, "Safe mode never deletes");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().HaveCount(3, "Alpha + Beta + Gamma");

            // Alpha keeps its v1 RtBlueprintAppliedAt because Safe does not update.
            // (Attribute-level data verification is covered by FirstTime tagging test;
            // here we rely on the diff counts to prove Safe was effective.)
            customers.Should().Contain(c => c.RtWellKnownName == "Alpha");
            customers.Should().Contain(c => c.RtWellKnownName == "Beta");
            customers.Should().Contain(c => c.RtWellKnownName == "Gamma");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_MergeMode_UpdatesLockedEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-merge");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge, null, ct);

            result.Success.Should().BeTrue();
            result.EntitiesAdded.Should().Be(1, "Gamma");
            result.EntitiesUpdated.Should().Be(1, "Alpha (locked)");
            result.EntitiesDeleted.Should().Be(0, "Merge does not delete");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().HaveCount(3, "Alpha + Beta + Gamma");

            // Alpha is still tagged as managed by this blueprint after the update.
            customers.Single(c => c.RtWellKnownName == "Alpha")
                .GetAttributeStringValueOrDefault("RtBlueprintSource")
                .Should().Be(TestBpV2.FullName, "Alpha is re-stamped with the v2 id");

            // Beta is no longer in seed but Merge never deletes.
            customers.Should().Contain(c => c.RtWellKnownName == "Beta");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_FullMode_DeletesOrphanLockedEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-full");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Full, null, ct);

            result.Success.Should().BeTrue();
            result.EntitiesAdded.Should().Be(1, "Gamma");
            result.EntitiesUpdated.Should().Be(1, "Alpha");
            result.EntitiesDeleted.Should().Be(1, "Beta is orphaned and locked");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().HaveCount(2, "Alpha + Gamma; Beta erased");
            customers.Should().NotContain(c => c.RtWellKnownName == "Beta");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task Rollback_AfterUpdate_RestoresPreviousState()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var backupService = _fixture.GetBackupService();
        var tenantId = await _fixture.CreateTestTenantAsync("rollback");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            // Create an explicit backup the test can roll back to.
            var backup = await backupService.CreateBackupAsync(tenantId, "before update", ct);

            var update = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Full,
                new BlueprintUpdateOptions { CreateBackup = false }, ct);
            update.Success.Should().BeTrue();

            (await QueryAllCustomersAsync(tenantId)).Should().HaveCount(2, "post-update: Alpha + Gamma");

            var restore = await blueprintService.RollbackAsync(tenantId, backup.BackupId, ct);
            restore.Success.Should().BeTrue();

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().HaveCount(2, "rolled back to TestBp-1.0.0: Alpha + Beta");
            customers.Should().Contain(c => c.RtWellKnownName == "Alpha");
            customers.Should().Contain(c => c.RtWellKnownName == "Beta");
            customers.Should().NotContain(c => c.RtWellKnownName == "Gamma");

            // After rollback the v1 blueprint id is on Alpha (v2's stamp is gone).
            customers.Single(c => c.RtWellKnownName == "Alpha")
                .GetAttributeStringValueOrDefault("RtBlueprintSource")
                .Should().Be(TestBpV1.FullName, "rollback restored Alpha's v1 tag");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyBlueprint_WithDependency_InstallsBothInOrderWithCorrectFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("multi");

        var rootId = new BlueprintId("TestRootBp", "1.0.0");
        var depId = new BlueprintId("TestDepBp", "1.0.0");

        try
        {
            // Apply the root — the resolver should pull in TestDepBp transitively.
            var result = await blueprintService.ApplyBlueprintAsync(tenantId, rootId, force: false, ct);
            result.IsSuccess.Should().BeTrue();

            // Both installations are recorded.
            var rows = await installations.GetInstalledAsync(tenantId, ct);
            rows.Should().HaveCount(2);

            var depRow = rows.Single(r => r.BlueprintId.Name == "TestDepBp");
            var rootRow = rows.Single(r => r.BlueprintId.Name == "TestRootBp");

            depRow.IsDependency.Should().BeTrue("transitive dep must be flagged as such");
            depRow.BlueprintId.Should().Be(depId);

            rootRow.IsDependency.Should().BeFalse("the explicitly-applied root is not a dependency");
            rootRow.BlueprintId.Should().Be(rootId);
            rootRow.ResolvedDependencies.Should().ContainSingle()
                .Which.Should().Be(depId);

            // Both seed entities are present in the tenant.
            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().Contain(c => c.RtWellKnownName == "DepCustomer");
            customers.Should().Contain(c => c.RtWellKnownName == "RootCustomer");

            // Each customer carries the provenance of its OWN blueprint, not the root.
            customers.Single(c => c.RtWellKnownName == "DepCustomer")
                .GetAttributeStringValueOrDefault("RtBlueprintSource")
                .Should().Be(depId.FullName);
            customers.Single(c => c.RtWellKnownName == "RootCustomer")
                .GetAttributeStringValueOrDefault("RtBlueprintSource")
                .Should().Be(rootId.FullName);
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyBlueprint_ReApply_DependencyAlreadyInstalled_StaysIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("multi-idem");

        var rootId = new BlueprintId("TestRootBp", "1.0.0");

        try
        {
            // First apply pulls in both rows.
            await blueprintService.ApplyBlueprintAsync(tenantId, rootId, force: false, ct);
            var firstRows = await installations.GetInstalledAsync(tenantId, ct);
            firstRows.Should().HaveCount(2);

            // Second apply without --force must not duplicate installation rows.
            await blueprintService.ApplyBlueprintAsync(tenantId, rootId, force: false, ct);
            var secondRows = await installations.GetInstalledAsync(tenantId, ct);
            secondRows.Should().HaveCount(2);
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task<List<RtEntity>> QueryAllCustomersAsync(string tenantId)
    {
        var repository = await _fixture.GetRuntimeRepositoryProvider()
            .GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();

        var session = await repository!.GetSessionAsync();
        var resultSet = await repository.GetRtEntitiesByTypeAsync(
            session, CustomerCkType, RtEntityQueryOptions.Create());
        return resultSet.Items.ToList();
    }

    private async Task UnlockCustomerAsync(string tenantId, string wellKnownName)
    {
        var repository = await _fixture.GetRuntimeRepositoryProvider()
            .GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();

        var session = await repository!.GetSessionAsync();
        var customers = await repository.GetRtEntitiesByTypeAsync(
            session, CustomerCkType, RtEntityQueryOptions.Create());

        var target = customers.Items.FirstOrDefault(c => c.RtWellKnownName == wellKnownName);
        target.Should().NotBeNull($"customer '{wellKnownName}' must exist before unlocking");

        target!.SetAttributeRawValue("RtBlueprintLocked", false);

        session.StartTransaction();
        await repository.ReplaceOneRtEntityByIdAsync(session, CustomerCkType, target.RtId, target);
        await session.CommitTransactionAsync();
    }
}
