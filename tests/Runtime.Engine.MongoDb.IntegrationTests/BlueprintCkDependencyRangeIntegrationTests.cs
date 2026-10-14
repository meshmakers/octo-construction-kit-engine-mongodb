using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.RangeRetention;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#6672: the CK model dependency ranges of a blueprint (<c>ckModelDependencies</c>) against CK models the tenant
///     already runs. The range floor is a minimum, never a target: a newer version inside the range is kept (no import,
///     the models are not even in a catalog), a version outside the range stops the apply before anything is
///     installed. Range-retaining (<c>RrBase</c>) and classic exact-pinned (<c>ClassicBase</c>) models side by side —
///     the rule is the same for both.
/// </summary>
[Collection(BlueprintServiceCollection.Name)]
public class BlueprintCkDependencyRangeIntegrationTests(BlueprintServiceFixture fixture)
{
    private static readonly BlueprintId CkRangeBp = new("TestCkRangeBp", "1.0.0");
    private static readonly BlueprintId CkRangeBpV2 = new("TestCkRangeBp", "2.0.0");

    [Theory]
    [InlineData("1.0.0", "1.0.0")] // at the floor
    [InlineData("1.2.0", "1.5.3")] // newer inside the range: kept
    public async Task Apply_NewerOrEqualInRangeCkModelsInstalled_AreKeptAndTheBlueprintInstalls(string rrVersion,
        string classicVersion)
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("ckrange-keep");
        try
        {
            await InstallAsync(tenantId, rrVersion, classicVersion);

            var result = await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, CkRangeBp, force: true, ct);

            result.IsSuccess.Should().BeTrue(string.Join("; ", result.OperationResult.Messages.Select(m => m.MessageText)));
            var versions = await fixture.GetRuntimeRepositoryProvider().GetSchemaVersionsAsync(tenantId, ct);
            versions["RrBase"].Should().Be(rrVersion, "a model inside its range is kept, not re-imported");
            versions["ClassicBase"].Should().Be(classicVersion);
            versions.Should().ContainKey("Test", "the model the tenant did not have is installed");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Theory]
    [InlineData("2.0.0", "1.2.0", "RrBase", "RrBase-[1.0,2.0)", "RrBase-2.0.0")]
    [InlineData("1.2.0", "3.1.0", "ClassicBase", "ClassicBase-[1.0,2.0)", "ClassicBase-3.1.0")]
    public async Task Apply_CkModelOutsideTheDeclaredRange_FailsNamingModelVersionAndRange_NothingIsInstalled(
        string rrVersion, string classicVersion, string offender, string expectedRange, string expectedInstalled)
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("ckrange-out");
        try
        {
            await InstallAsync(tenantId, rrVersion, classicVersion);

            var result = await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, CkRangeBp, force: true, ct);

            result.IsSuccess.Should().BeFalse();
            var message = result.OperationResult.Messages.Should()
                .ContainSingle(m => m.MessageText.Contains(offender)).Which.MessageText;
            message.Should().Contain(expectedRange).And.Contain(expectedInstalled).And.Contain("nothing was installed");

            var versions = await fixture.GetRuntimeRepositoryProvider().GetSchemaVersionsAsync(tenantId, ct);
            versions["RrBase"].Should().Be(rrVersion, "nothing is downgraded");
            versions["ClassicBase"].Should().Be(classicVersion);
            versions.Should().NotContainKey("Test", "the apply stops before any CK model of the blueprint is installed");
            (await fixture.GetService<ITenantBlueprintInstallations>().GetByBlueprintNameAsync(tenantId, CkRangeBp.Name, ct))
                .Should().BeNull("no installation row is written");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    // The update path shows the same behaviours as the first install (AB#6672).
    [Fact]
    public async Task ApplyUpdate_NewerInRangeCkModelsInstalled_AreKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("ckrange-upd");
        try
        {
            await InstallAsync(tenantId, "1.2.0", "1.5.3");
            (await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, CkRangeBp, force: false, ct))
                .IsSuccess.Should().BeTrue();

            var result = await fixture.GetBlueprintService().ApplyUpdateAsync(tenantId, CkRangeBpV2,
                BlueprintUpdateMode.Merge, null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            var versions = await fixture.GetRuntimeRepositoryProvider().GetSchemaVersionsAsync(tenantId, ct);
            versions["RrBase"].Should().Be("1.2.0");
            versions["ClassicBase"].Should().Be("1.5.3");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_CkModelMovedOutsideTheDeclaredRange_FailsAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("ckrange-updout");
        try
        {
            await InstallAsync(tenantId, "1.2.0", "1.5.3");
            (await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, CkRangeBp, force: false, ct))
                .IsSuccess.Should().BeTrue();
            // The tenant drifted: the range-retaining model now runs in a new major, outside [1.0,2.0).
            var tenant = (await fixture.GetSystemContext().TryFindTenantContextAsync(tenantId))!;
            await tenant.ImportCkModelAsync(Base("RrBase", "2.0.0", await GetInstalledSystemIdAsync(tenant),
                rangeRetaining: true));

            var result = await fixture.GetBlueprintService().ApplyUpdateAsync(tenantId, CkRangeBpV2,
                BlueprintUpdateMode.Merge, null, ct);

            result.Success.Should().BeFalse();
            result.Errors.Should().ContainSingle(e => e.Contains("RrBase-[1.0,2.0)") && e.Contains("RrBase-2.0.0"));
            (await fixture.GetRuntimeRepositoryProvider().GetSchemaVersionsAsync(tenantId, ct))["RrBase"]
                .Should().Be("2.0.0", "nothing is downgraded");
            (await fixture.GetService<ITenantBlueprintInstallations>().GetByBlueprintNameAsync(tenantId, CkRangeBp.Name, ct))!
                .BlueprintId.Should().Be(CkRangeBp, "the installation row is not moved to the failed target");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task InstallAsync(string tenantId, string rrVersion, string classicVersion)
    {
        var tenant = (await fixture.GetSystemContext().TryFindTenantContextAsync(tenantId))!;
        var system = await GetInstalledSystemIdAsync(tenant);
        await tenant.ImportCkModelAsync(Base("RrBase", rrVersion, system, rangeRetaining: true));
        await tenant.ImportCkModelAsync(Base("ClassicBase", classicVersion, system, rangeRetaining: false));
    }

    private async Task<CkModelId> GetInstalledSystemIdAsync(ITenantContext tenant)
    {
        var catalogService = fixture.GetService<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await tenant.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found in the catalogs.");
    }

    /// <summary>A minimal model with one type derived from System/Entity; range-retaining or exact-pinned.</summary>
    private static CkCompiledModelRoot Base(string name, string version, CkModelId system, bool rangeRetaining)
    {
        var id = new CkModelId($"{name}-{version}");
        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "AB#6672 model the tenant already runs",
            Dependencies = [system],
            DependencyRanges = rangeRetaining
                ?
                [
                    new CkModelDependencyDto
                    {
                        Range = new CkModelIdVersionRange(system.Name,
                            $"[{system.Version.Major}.0,{system.Version.Major + 1}.0)"),
                        Floor = system.Version.ToString()
                    }
                ]
                : null,
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Thing-1"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>(rangeRetaining ? system.ToMajorQualified() : system,
                        new CkTypeId("Entity-1"))
                }
            ]
        };
    }
}
