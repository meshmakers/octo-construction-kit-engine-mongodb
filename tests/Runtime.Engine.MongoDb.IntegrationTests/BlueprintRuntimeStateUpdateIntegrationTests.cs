using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8684: a blueprint update must not reset what the tenant did with a seeded entity after the install. The shape is
///     the one of a seeded pipeline (System.Communication/Pipeline): a seed-owned definition plus a runtime-state
///     deployment state that the seed declares explicitly with its default (<c>DeploymentState: 0</c>). Every UpdateBlueprint
///     of MeshmakersAccounting showed the pipelines with the update time as creation date and UNDEPLOYED.
///     <c>DeployStateBp</c> 1.0.0 -> 2.0.0 keeps item ...a1 unchanged, changes the definition of item ...a2 and carries
///     an entity with a string runtime state (AB#4582) whose seed value never changes.
/// </summary>
[Collection(BlueprintServiceCollection.Name)]
public class BlueprintRuntimeStateUpdateIntegrationTests(BlueprintServiceFixture fixture)
{
    private const int Deployed = 2;

    private static readonly BlueprintId DeployStateBpV1 = new("DeployStateBp", "1.0.0");
    private static readonly BlueprintId DeployStateBpV2 = new("DeployStateBp", "2.0.0");
    private static readonly RtCkId<CkTypeId> DeployableItemCkType = new("Test/DeployableItem");
    private static readonly RtCkId<CkTypeId> OwnershipEntityCkType = new("Test/OwnershipEntity");
    private static readonly OctoObjectId UnchangedItemId = new("67d4a2f0b2e4d8c3a1f084a1");
    private static readonly OctoObjectId ChangedItemId = new("67d4a2f0b2e4d8c3a1f084a2");
    private static readonly OctoObjectId SyncEntityId = new("67d4a2f0b2e4d8c3a1f084a3");

    [Theory]
    [InlineData(BlueprintUpdateMode.Merge)]
    [InlineData(BlueprintUpdateMode.Full)]
    public async Task ApplyUpdate_UnchangedDeployedItem_KeepsRtIdCreationDateAndDeployState(BlueprintUpdateMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync($"rs-unchanged-{mode}".ToLowerInvariant());

        try
        {
            await InstallAndDeployAsync(blueprintService, tenantId, ct);
            var before = await GetAsync(tenantId, DeployableItemCkType, UnchangedItemId);

            var result = await blueprintService.ApplyUpdateAsync(tenantId, DeployStateBpV2, mode, null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            var after = await GetAsync(tenantId, DeployableItemCkType, UnchangedItemId);
            after.RtId.Should().Be(before.RtId);
            DeployStateOf(after).Should().Be(Deployed, "the deployment state belongs to the tenant, not to the seed");
            after.RtCreationDateTime.Should().Be(before.RtCreationDateTime,
                "an upsert of an existing entity is no new creation");
            after.GetAttributeStringValueOrDefault("RtBlueprintSource").Should().Be(DeployStateBpV2.FullName);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Theory]
    [InlineData(BlueprintUpdateMode.Merge)]
    [InlineData(BlueprintUpdateMode.Full)]
    public async Task ApplyUpdate_ChangedDeployedItem_TakesNewDefinitionAndKeepsDeployState(BlueprintUpdateMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync($"rs-changed-{mode}".ToLowerInvariant());

        try
        {
            await InstallAndDeployAsync(blueprintService, tenantId, ct);
            var before = await GetAsync(tenantId, DeployableItemCkType, ChangedItemId);

            var result = await blueprintService.ApplyUpdateAsync(tenantId, DeployStateBpV2, mode, null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            var after = await GetAsync(tenantId, DeployableItemCkType, ChangedItemId);
            after.RtId.Should().Be(before.RtId);
            after.GetAttributeStringValueOrDefault("ItemDefinition").Should().Be("triggers: [import, export]",
                "the definition is seed-owned and follows the new version");
            DeployStateOf(after).Should().Be(Deployed);
            after.RtCreationDateTime.Should().Be(before.RtCreationDateTime);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_EntityWithStringRuntimeState_KeepsTenantValue_AB4582()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("rs-ab4582");

        try
        {
            await InstallAndDeployAsync(blueprintService, tenantId, ct);
            var before = await GetAsync(tenantId, OwnershipEntityCkType, SyncEntityId);

            var result = await blueprintService.ApplyUpdateAsync(tenantId, DeployStateBpV2, BlueprintUpdateMode.Merge,
                null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            var after = await GetAsync(tenantId, OwnershipEntityCkType, SyncEntityId);
            after.GetAttributeStringValueOrDefault("SyncCursor").Should().Be("cursor-42",
                "runtime state survives the blueprint path, not only a direct ImportRt (AB#4582)");
            after.GetAttributeStringValueOrDefault("Name").Should().Be("Sync 2.0.0", "seed-owned values follow the seed");
            after.RtCreationDateTime.Should().Be(before.RtCreationDateTime);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ForcedReApply_DeployedItems_KeepCreationDateAndDeployState()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("rs-reapply");

        try
        {
            await InstallAndDeployAsync(blueprintService, tenantId, ct);
            var before = await GetAsync(tenantId, DeployableItemCkType, UnchangedItemId);

            var result = await blueprintService.ApplyBlueprintAsync(tenantId, DeployStateBpV1, force: true, ct);

            result.IsSuccess.Should().BeTrue(string.Join("; ", result.OperationResult.Messages.Select(m => m.MessageText)));
            var after = await GetAsync(tenantId, DeployableItemCkType, UnchangedItemId);
            DeployStateOf(after).Should().Be(Deployed);
            DeployStateOf(await GetAsync(tenantId, DeployableItemCkType, ChangedItemId)).Should().Be(Deployed);
            (await GetAsync(tenantId, OwnershipEntityCkType, SyncEntityId))
                .GetAttributeStringValueOrDefault("SyncCursor").Should().Be("cursor-42");
            after.RtCreationDateTime.Should().Be(before.RtCreationDateTime);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    ///     Installs 1.0.0 and then plays the deployment: both items are Deployed, the sync entity holds a cursor. The
    ///     creation dates are moved into the past so that a reset to "now" cannot pass unnoticed.
    /// </summary>
    private async Task InstallAndDeployAsync(IBlueprintService blueprintService, string tenantId, CancellationToken ct)
    {
        var install = await blueprintService.ApplyBlueprintAsync(tenantId, DeployStateBpV1, force: false, ct);
        install.IsSuccess.Should().BeTrue(string.Join("; ", install.OperationResult.Messages.Select(m => m.MessageText)));

        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await UpdateAsync(tenantId, DeployableItemCkType, UnchangedItemId, e =>
        {
            e.SetAttributeRawValue("ItemDeployState", Deployed);
            e.RtCreationDateTime = created;
        });
        await UpdateAsync(tenantId, DeployableItemCkType, ChangedItemId, e =>
        {
            e.SetAttributeRawValue("ItemDeployState", Deployed);
            e.RtCreationDateTime = created;
        });
        await UpdateAsync(tenantId, OwnershipEntityCkType, SyncEntityId, e =>
        {
            e.SetAttributeRawValue("SyncCursor", "cursor-42");
            e.RtCreationDateTime = created;
        });

        DeployStateOf(await GetAsync(tenantId, DeployableItemCkType, UnchangedItemId)).Should().Be(Deployed);
    }

    private static int? DeployStateOf(RtEntity entity)
    {
        return entity.Attributes.TryGetValue("ItemDeployState", out var value) && value != null
            ? Convert.ToInt32(value)
            : null;
    }

    private async Task<RtEntity> GetAsync(string tenantId, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();

        using var session = await repository!.GetSessionAsync();
        var result = await repository.GetRtEntitiesByIdAsync(session, ckTypeId, [rtId], RtEntityQueryOptions.Create());
        return result.Items.Should().ContainSingle().Subject;
    }

    private async Task UpdateAsync(string tenantId, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId,
        Action<RtEntity> change)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();

        var entity = await GetAsync(tenantId, ckTypeId, rtId);
        change(entity);

        using var session = await repository!.GetSessionAsync();
        session.StartTransaction();
        await repository.ReplaceOneRtEntityByIdAsync(session, ckTypeId, rtId, entity);
        await session.CommitTransactionAsync();
    }
}
