using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// AB#9651 against a real MongoDB tenant. A blueprint whose seed links entities to an entity another blueprint seeds
/// later - in production the pipelines of a blueprint and the Mesh Adapter of the service-managed System.Communication
/// blueprint, which runs when the platform enables Communication after the install (AB#9578) - ends with every edge
/// present, exactly as when the target existed before the install. <c>DeferredLinkBp</c> stands in for the blueprint
/// with the adapter-linked pipelines, <c>DeferredTargetBp</c> for the System.Communication seed.
/// </summary>
[Collection(BlueprintServiceCollection.Name)]
public class BlueprintDeferredSeedAssociationIntegrationTests(BlueprintServiceFixture fixture)
{
    private static readonly BlueprintId LinkBp = new("DeferredLinkBp", "1.0.0");
    private static readonly BlueprintId TargetBp = new("DeferredTargetBp", "1.0.0");
    private static readonly RtCkId<CkTypeId> CountryCkType = new("Test/Country");
    private static readonly RtCkId<CkTypeId> ConfigurationCkType = new("System", "TenantConfiguration");
    private static readonly OctoObjectId Europe = new("507f1f77bcf86cd7994396a0");
    private const string StoreKey = "Octo.Blueprints.DeferredSeedAssociations";

    [Fact]
    public async Task TargetSeededAfterTheInstall_EveryEdgeIsWritten_AndTheInstallReportedItAsAWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("deferred-after");

        try
        {
            var install = await blueprintService.ApplyBlueprintAsync(tenantId, LinkBp, force: false, ct);
            install.IsSuccess.Should().BeTrue(Messages(install));
            var warning = install.OperationResult.Messages.Should()
                .ContainSingle(m => m.MessageNumber == 73, "no silent loss: the kept edges are reported").Subject;
            warning.MessageLevel.Should().Be(MessageLevel.Warning);
            warning.MessageText.Should().Contain("2 association edge(s)").And.Contain(Europe.ToString());
            (await ParentTargetsAsync(tenantId)).Should().BeEmpty("the continent does not exist yet");
            (await StoreCountAsync(tenantId)).Should().Be(1);

            // The later seed (System.Communication when Communication is enabled) brings the target.
            var target = await blueprintService.ApplyBlueprintAsync(tenantId, TargetBp, force: false, ct);
            target.IsSuccess.Should().BeTrue(Messages(target));
            target.OperationResult.Messages.Should().ContainSingle(m => m.MessageNumber == 74)
                .Which.MessageText.Should().Contain("Wrote 2 deferred association edge(s)").And.Contain(LinkBp.FullName);

            (await ParentTargetsAsync(tenantId)).Should().BeEquivalentTo([Europe, Europe],
                "both countries end linked exactly as when the continent existed before the install");
            (await StoreCountAsync(tenantId)).Should().Be(0, "nothing is left to wait for");

            // A re-apply of the target writes nothing twice and reports nothing.
            var again = await blueprintService.ApplyBlueprintAsync(tenantId, TargetBp, force: true, ct);
            again.IsSuccess.Should().BeTrue(Messages(again));
            again.OperationResult.Messages.Should().NotContain(m => m.MessageNumber == 73 || m.MessageNumber == 74);
            (await ParentTargetsAsync(tenantId)).Should().HaveCount(2);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task TargetSeededBeforeTheInstall_EveryEdgeIsWrittenAtOnce_WithoutWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("deferred-before");

        try
        {
            (await blueprintService.ApplyBlueprintAsync(tenantId, TargetBp, force: false, ct)).IsSuccess.Should().BeTrue();

            var install = await blueprintService.ApplyBlueprintAsync(tenantId, LinkBp, force: false, ct);

            install.IsSuccess.Should().BeTrue(Messages(install));
            install.OperationResult.Messages.Should().NotContain(m => m.MessageNumber == 73 || m.MessageNumber == 74);
            (await ParentTargetsAsync(tenantId)).Should().BeEquivalentTo([Europe, Europe]);
            (await StoreCountAsync(tenantId)).Should().Be(0);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task UninstallBeforeTheTargetExists_ForgetsTheKeptEdges()
    {
        var ct = TestContext.Current.CancellationToken;
        var blueprintService = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("deferred-uninstall");

        try
        {
            (await blueprintService.ApplyBlueprintAsync(tenantId, LinkBp, force: false, ct)).IsSuccess.Should().BeTrue();
            (await StoreCountAsync(tenantId)).Should().Be(1);

            var uninstall = await blueprintService.UninstallAsync(tenantId, LinkBp.Name,
                cancellationToken: ct);
            uninstall.Success.Should().BeTrue(string.Join("; ", uninstall.Errors));

            (await StoreCountAsync(tenantId)).Should().Be(0);
            var target = await blueprintService.ApplyBlueprintAsync(tenantId, TargetBp, force: false, ct);
            target.OperationResult.Messages.Should().NotContain(m => m.MessageNumber == 74);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private static string Messages(BlueprintApplicationResult result) =>
        string.Join("; ", result.OperationResult.Messages.Select(m => $"[{m.MessageLevel}] {m.MessageText}"));

    private async Task<List<OctoObjectId>> ParentTargetsAsync(string tenantId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        using var session = await repository!.GetSessionAsync();
        var countries = await repository.GetRtEntitiesByTypeAsync(session, CountryCkType, RtEntityQueryOptions.Create());
        countries.Items.Should().HaveCount(2);
        var associations = await repository.GetRtAssociationsAsync(session,
            countries.Items.Select(c => c.ToRtEntityId()).ToList(),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, 0, 10));
        return associations.Values.SelectMany(v => v.Items).Select(a => a.TargetRtId).ToList();
    }

    private async Task<int> StoreCountAsync(string tenantId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        using var session = await repository!.GetSessionAsync();
        var options = RtEntityQueryOptions.Create();
        options.Field(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.Equals, StoreKey);
        var result = await repository.GetRtEntitiesByTypeAsync(session, ConfigurationCkType, options);
        return result.Items.Count();
    }
}
