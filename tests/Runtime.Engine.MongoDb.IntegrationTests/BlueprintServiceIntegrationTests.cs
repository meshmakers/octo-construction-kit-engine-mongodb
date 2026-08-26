using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

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
[Collection(BlueprintServiceCollection.Name)]
public class BlueprintServiceIntegrationTests(BlueprintServiceFixture fixture)
{
    private readonly BlueprintServiceFixture _fixture = fixture;

    private static readonly BlueprintId TestBpV1 = new("TestBp", "1.0.0");
    private static readonly BlueprintId TestBpV2 = new("TestBp", "2.0.0");
    private static readonly BlueprintId TestMigBpV1 = new("TestMigBp", "1.0.0");
    private static readonly BlueprintId TestMigBpV2 = new("TestMigBp", "2.0.0");
    private static readonly RtCkId<CkTypeId> CustomerCkType = new("Test/Customer");
    private static readonly RtCkId<CkTypeId> ContinentCkType = new("Test/Continent");

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
            result.EntitiesCreated.Should().Be(4, "TestBp-1.0.0 seeds two customers and two continents");

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
    public async Task ApplyUpdate_MergeMode_RecordsTargetVersionInInstallationRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-inst-row");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var beforeUpdate = await installations.GetByBlueprintNameAsync(tenantId, "TestBp", ct);
            beforeUpdate.Should().NotBeNull();
            beforeUpdate!.BlueprintId.Should().Be(TestBpV1);

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge, null, ct);
            result.Success.Should().BeTrue();

            var rows = await installations.GetInstalledAsync(tenantId, ct);
            rows.Should().HaveCount(1, "an update must not add a second row for the same blueprint");

            var row = rows.Single();
            row.BlueprintId.Should().Be(TestBpV2,
                "the installation row is the live view and must name the version now in effect");
            row.InstalledAt.Should().Be(beforeUpdate.InstalledAt,
                "InstalledAt is the first-install timestamp and is never overwritten");
            row.LastUpdatedAt.Should().BeAfter(beforeUpdate.LastUpdatedAt,
                "the update must stamp LastUpdatedAt");
            row.IsDependency.Should().BeFalse("TestBp was applied explicitly, not as a dependency");
            // Same apply timestamp for both records; BSON stores DateTime with millisecond
            // precision, so the persisted value is the in-memory one truncated.
            row.LastUpdatedAt.Should().BeCloseTo(result.NewBlueprintInfo!.AppliedAt,
                TimeSpan.FromMilliseconds(1),
                "installation row and history entry share the apply timestamp");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_DryRun_WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var history = _fixture.GetBlueprintHistory();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-inst-dryrun");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var rowBefore = await installations.GetByBlueprintNameAsync(tenantId, "TestBp", ct);
            rowBefore.Should().NotBeNull();
            var historyCountBefore = (await history.GetHistoryAsync(tenantId, ct)).Count;

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge,
                new BlueprintUpdateOptions { DryRun = true }, ct);

            result.Success.Should().BeTrue();

            // A dry run is a promise about every write path, not just the installation row.
            var row = await installations.GetByBlueprintNameAsync(tenantId, "TestBp", ct);
            row.Should().NotBeNull();
            row!.BlueprintId.Should().Be(TestBpV1, "the installed version must not move");
            row.InstalledAt.Should().Be(rowBefore!.InstalledAt);
            row.LastUpdatedAt.Should().Be(rowBefore.LastUpdatedAt,
                "even a same-version upsert would be a write and must not happen");

            (await history.GetHistoryAsync(tenantId, ct)).Count
                .Should().Be(historyCountBefore, "a dry run must not append a history entry");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().NotContain(c => c.RtWellKnownName == "Gamma",
                "the v2 seed must not be applied");
            customers.Should().HaveCount(2, "Alpha + Beta, exactly as v1 left them");
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
    public async Task ApplyUpdate_KeepBlueprint_PromotesUserModifiedConflictInCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-keepbp-um");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);
            await UnlockCustomerAsync(tenantId, "Alpha");

            var alphaBefore = (await QueryAllCustomersAsync(tenantId))
                .Single(c => c.RtWellKnownName == "Alpha");
            var alphaRtId = alphaBefore.RtId.ToString();

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge,
                new BlueprintUpdateOptions
                {
                    ConflictResolutions = new Dictionary<string, ConflictResolution>
                    {
                        [alphaRtId!] = ConflictResolution.KeepBlueprint
                    }
                }, ct);

            result.Success.Should().BeTrue();
            result.EntitiesUpdated.Should().Be(1, "Alpha is promoted via KeepBlueprint");
            result.EntitiesSkipped.Should().Be(0, "no remaining Skip resolutions");

            var alphaAfter = (await QueryAllCustomersAsync(tenantId))
                .Single(c => c.RtWellKnownName == "Alpha");
            alphaAfter.GetAttributeStringValueOrDefault("RtBlueprintSource")
                .Should().Be(TestBpV2.FullName, "Alpha is re-stamped with v2");
            alphaAfter.GetAttributeValueOrDefault<bool>("RtBlueprintLocked")
                .Should().BeTrue("KeepBlueprint re-locks the entity");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_KeepBlueprint_PromotesDeleteModifiedConflictInFullMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("apply-keepbp-dm");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);
            // Beta is missing from v2 — unlocking makes the v2 update raise a
            // DeleteModified conflict instead of erasing the orphan outright.
            await UnlockCustomerAsync(tenantId, "Beta");

            var betaBefore = (await QueryAllCustomersAsync(tenantId))
                .Single(c => c.RtWellKnownName == "Beta");
            var betaRtId = betaBefore.RtId.ToString();

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Full,
                new BlueprintUpdateOptions
                {
                    ConflictResolutions = new Dictionary<string, ConflictResolution>
                    {
                        [betaRtId!] = ConflictResolution.KeepBlueprint
                    }
                }, ct);

            result.Success.Should().BeTrue();
            result.EntitiesDeleted.Should().Be(1, "Beta is promoted to delete via KeepBlueprint");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().NotContain(c => c.RtWellKnownName == "Beta",
                "DeleteModified + KeepBlueprint erases the entity");
            customers.Should().Contain(c => c.RtWellKnownName == "Alpha");
            customers.Should().Contain(c => c.RtWellKnownName == "Gamma");
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

    [Fact]
    public async Task Uninstall_BlueprintWithoutDependents_RemovesLockedEntitiesAndInstallationRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("uninst-leaf");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            (await QueryAllCustomersAsync(tenantId)).Should().HaveCount(2);

            var result = await blueprintService.UninstallAsync(tenantId, "TestBp", cascade: false, ct);

            result.Success.Should().BeTrue();
            result.EntitiesDeleted.Should().Be(4, "two locked customers plus two locked continents");
            result.UninstalledBlueprintId.Should().Be(TestBpV1);

            (await installations.GetByBlueprintNameAsync(tenantId, "TestBp", ct))
                .Should().BeNull("installation row removed");

            (await QueryAllCustomersAsync(tenantId)).Should().BeEmpty("locked owned entities erased");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task Uninstall_AfterUpdate_RemovesEntitiesOfUpdatedVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("uninst-updated");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var update = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Merge, null, ct);
            update.Success.Should().BeTrue();

            var result = await blueprintService.UninstallAsync(tenantId, "TestBp", cascade: false, ct);

            result.Success.Should().BeTrue();
            result.UninstalledBlueprintId.Should().Be(TestBpV2,
                "uninstall works off the installation row, which the update advanced to v2");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().NotContain(c => c.RtWellKnownName == "Alpha",
                "Alpha was re-stamped with the v2 id by the update and is owned by the uninstalled version");
            customers.Should().NotContain(c => c.RtWellKnownName == "Gamma",
                "Gamma only exists in the v2 seed");

            // Not asserted on purpose: entities the target version dropped (Beta, and the
            // v1 continents) currently survive, because uninstall only walks the seed of
            // the installed version. That is a known gap — pinning it here would turn a
            // future uninstall improvement into a failing test.
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task Uninstall_BlueprintWithDependents_RefusesWithoutCascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("uninst-refuse");

        try
        {
            await blueprintService.ApplyBlueprintAsync(
                tenantId, new BlueprintId("TestRootBp", "1.0.0"), force: false, ct);

            // Attempt to uninstall the dep — TestRootBp still depends on it.
            var result = await blueprintService.UninstallAsync(tenantId, "TestDepBp", cascade: false, ct);

            result.Success.Should().BeFalse();
            result.BlockingDependents.Should().ContainSingle()
                .Which.Name.Should().Be("TestRootBp");

            // Nothing should have changed.
            (await installations.GetInstalledAsync(tenantId, ct)).Should().HaveCount(2);
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task Uninstall_Cascade_RemovesDependentsAndOrphanDependencies()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("uninst-cascade");

        try
        {
            await blueprintService.ApplyBlueprintAsync(
                tenantId, new BlueprintId("TestRootBp", "1.0.0"), force: false, ct);

            (await installations.GetInstalledAsync(tenantId, ct)).Should().HaveCount(2);

            // Cascade-uninstall the dep — root depends on it, so root gets removed first,
            // then the dep itself.
            var result = await blueprintService.UninstallAsync(tenantId, "TestDepBp", cascade: true, ct);

            result.Success.Should().BeTrue();
            // The dependent (TestRootBp) was cascaded away.
            result.CascadedDependencies.Should()
                .Contain(d => d.Name == "TestRootBp", "TestRootBp depended on TestDepBp");

            (await installations.GetInstalledAsync(tenantId, ct)).Should().BeEmpty(
                "cascade removed both blueprints");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task Uninstall_Root_CascadesOrphanedDependency()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("uninst-orphan");

        try
        {
            await blueprintService.ApplyBlueprintAsync(
                tenantId, new BlueprintId("TestRootBp", "1.0.0"), force: false, ct);

            // Uninstall the root with cascade — TestDepBp was pulled in as IsDependency=true
            // and has no other referrers, so cascade should auto-clean it up.
            var result = await blueprintService.UninstallAsync(tenantId, "TestRootBp", cascade: true, ct);

            result.Success.Should().BeTrue();
            result.CascadedDependencies.Should()
                .Contain(d => d.Name == "TestDepBp", "TestDepBp orphaned, IsDependency=true");

            (await installations.GetInstalledAsync(tenantId, ct)).Should().BeEmpty();
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task Uninstall_NonexistentBlueprint_ReturnsFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("uninst-missing");

        try
        {
            var result = await blueprintService.UninstallAsync(tenantId, "NoSuchBp", cascade: false, ct);

            result.Success.Should().BeFalse();
            result.UninstalledBlueprintId.Should().BeNull();
            result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// AB#4832: on a tenant carrying more than one blueprint, the update path used to resolve
    /// "the current version" from the newest history entry of the whole tenant. The migration
    /// lookup therefore never matched and the update degraded to Merge with a warning, while
    /// still reporting success. Merge cannot delete, so the deleted legacy customer is the
    /// discriminator between "the script ran" and the old silent fallback.
    /// </summary>
    [Fact]
    public async Task ApplyUpdate_MigrationMode_MultiBlueprintTenant_ExecutesScript()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("mig-multi-bp");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestMigBpV1, force: false, ct);

            // A second, unrelated blueprint applied afterwards - this is what the name-less
            // history lookup used to return as "the current version".
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var history = _fixture.GetBlueprintHistory();
            var lastOfTenant = await history.GetCurrentAsync(tenantId, ct);
            lastOfTenant!.BlueprintId.Should().Be(TestBpV1,
                "precondition: the newest history entry of the tenant belongs to the other blueprint");

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestMigBpV2, BlueprintUpdateMode.Migration, null, ct);

            result.Success.Should().BeTrue(
                "the migration script for 1.0.0 must be found even though another blueprint was applied later: "
                + string.Join(" | ", result.Errors));
            result.Errors.Should().BeEmpty();
            result.Warnings.Should().NotContain(w => w.Contains("No migration script found"),
                "the script exists for the installed version - no Merge fallback");

            result.NewBlueprintInfo.Should().NotBeNull();
            result.NewBlueprintInfo!.ApplicationMode.Should().Be(BlueprintApplicationMode.Migration);
            result.NewBlueprintInfo.PreviousVersion.Should().Be(TestMigBpV1,
                "PreviousVersion is the previous version of *this* blueprint, not of whatever was applied last");

            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().NotContain(c => c.RtWellKnownName == "MigLegacy",
                "the Delete step of the migration script must have run - Merge mode never deletes");

            var continents = await QueryAllAsync(tenantId, ContinentCkType);
            var region = continents.Single(c => c.RtWellKnownName == "MigRegion");
            region.GetAttributeStringValueOrDefault("Name").Should().Be("Region renamed by migration",
                "the Update step of the migration script must have run - the value appears in no seed file");

            customers.Should().Contain(c => c.RtWellKnownName == "MigKept");

            // The other blueprint is untouched by the update of this one.
            customers.Should().Contain(c => c.RtWellKnownName == "Alpha");
            customers.Should().Contain(c => c.RtWellKnownName == "Beta");

            var currentOfMigBp = await history.GetCurrentByBlueprintNameAsync(tenantId, "TestMigBp", ct);
            currentOfMigBp!.BlueprintId.Should().Be(TestMigBpV2);
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// AB#4832: an explicitly requested Migration update without a matching script must fail
    /// loudly instead of quietly applying the seed data in Merge mode and reporting success.
    /// </summary>
    [Fact]
    public async Task ApplyUpdate_MigrationMode_WithoutMatchingScript_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var installations = _fixture.GetService<ITenantBlueprintInstallations>();
        var tenantId = await _fixture.CreateTestTenantAsync("mig-no-script");

        try
        {
            // TestBp ships no migrations at all.
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var result = await blueprintService.ApplyUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Migration, null, ct);

            result.Success.Should().BeFalse();
            result.Errors.Should().NotBeEmpty();
            var errorText = string.Join(" | ", result.Errors);
            errorText.Should().Contain("1.0.0", "the error names the installed version");

            // Neither record moved on.
            var row = await installations.GetByBlueprintNameAsync(tenantId, "TestBp", ct);
            row!.BlueprintId.Should().Be(TestBpV1);

            var history = _fixture.GetBlueprintHistory();
            var current = await history.GetCurrentByBlueprintNameAsync(tenantId, "TestBp", ct);
            current!.BlueprintId.Should().Be(TestBpV1);

            // The seed diff of 2.0.0 must not have been applied either.
            var customers = await QueryAllCustomersAsync(tenantId);
            customers.Should().Contain(c => c.RtWellKnownName == "Beta",
                "Beta is dropped by the 2.0.0 seed - nothing may have been imported");
            customers.Should().NotContain(c => c.RtWellKnownName == "Gamma",
                "Gamma only exists in the 2.0.0 seed, which must not have been imported");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// AB#4832: the preview agrees with the apply - a missing migration script is a blocking
    /// conflict, not a warning that scrolls past.
    /// </summary>
    [Fact]
    public async Task PreviewUpdate_MigrationMode_WithoutMatchingScript_ReportsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var tenantId = await _fixture.CreateTestTenantAsync("mig-preview-no-script");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var preview = await blueprintService.PreviewUpdateAsync(
                tenantId, TestBpV2, BlueprintUpdateMode.Migration, ct);

            var conflict = preview.Conflicts.Should()
                .ContainSingle(c => c.ConflictType == ConflictType.MissingMigrationScript).Subject;
            conflict.EntityId.Should().Be("migration:" + TestBpV2.FullName);
            conflict.Description.Should().Contain("1.0.0");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// AB#4832: the name-filtered history query itself, against a real MongoDB tenant - the
    /// compound (BlueprintName, AppliedAt) index on System/BlueprintHistory is what makes it
    /// answer per blueprint.
    /// </summary>
    [Fact]
    public async Task GetCurrentByBlueprintName_ReturnsVersionOfThatBlueprintOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = _fixture.GetBlueprintService();
        var history = _fixture.GetBlueprintHistory();
        var tenantId = await _fixture.CreateTestTenantAsync("hist-by-name");

        try
        {
            await blueprintService.ApplyBlueprintAsync(tenantId, TestMigBpV1, force: false, ct);
            await blueprintService.ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            (await history.GetCurrentByBlueprintNameAsync(tenantId, "TestMigBp", ct))!
                .BlueprintId.Should().Be(TestMigBpV1);
            (await history.GetCurrentByBlueprintNameAsync(tenantId, "TestBp", ct))!
                .BlueprintId.Should().Be(TestBpV1);
            (await history.GetCurrentByBlueprintNameAsync(tenantId, "NotInstalledBp", ct))
                .Should().BeNull();
            (await history.GetCurrentAsync(tenantId, ct))!
                .BlueprintId.Should().Be(TestBpV1, "the name-less lookup keeps its last-applied semantics");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    private Task<List<RtEntity>> QueryAllCustomersAsync(string tenantId)
    {
        return QueryAllAsync(tenantId, CustomerCkType);
    }

    private async Task<List<RtEntity>> QueryAllAsync(string tenantId, RtCkId<CkTypeId> ckTypeId)
    {
        var repository = await _fixture.GetRuntimeRepositoryProvider()
            .GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();

        // GetSessionAsync starts a new server session per call - dispose it, or a shared
        // helper like this one piles them up over a full integration run.
        using var session = await repository!.GetSessionAsync();
        var resultSet = await repository.GetRtEntitiesByTypeAsync(
            session, ckTypeId, RtEntityQueryOptions.Create());
        return resultSet.Items.ToList();
    }

    private async Task UnlockCustomerAsync(string tenantId, string wellKnownName)
    {
        var repository = await _fixture.GetRuntimeRepositoryProvider()
            .GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();

        using var session = await repository!.GetSessionAsync();
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
