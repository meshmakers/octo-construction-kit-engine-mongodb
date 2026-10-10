using System.Collections;

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
/// AB#6313 / AB#6395: the seed value guard of the Upsert import, end to end against a real MongoDB
/// tenant. A blueprint update goes through <c>ImportRtModelCommand</c> (Upsert), the single choke
/// point that must never replace a non-empty tenant value of a SeedOwned attribute with an empty,
/// omitted or default seed value.
/// <para>
/// Blueprints (TestBlueprints/SeedGuardBp): 1.0.0 fills every attribute, 2.0.0 brings empty or
/// omitted values for the same entity, 3.0.0 brings different non-empty values.
/// </para>
/// </summary>
[Collection(SeedValueGuardCollection.Name)]
public class SeedValueGuardBlueprintIntegrationTests(BlueprintServiceFixture fixture)
{
    private static readonly BlueprintId GuardBpV1 = new("SeedGuardBp", "1.0.0");
    private static readonly BlueprintId GuardBpV2 = new("SeedGuardBp", "2.0.0");
    private static readonly BlueprintId GuardBpV3 = new("SeedGuardBp", "3.0.0");
    private static readonly RtCkId<CkTypeId> GuardCkType = new("Test/SeedGuardEntity");

    private const string TenantEditedText = "edited by the tenant";

    // What 1.0.0 seeds (the tenant then holds it).
    private const string V1Config =
        "{\"Host\":\"eda.example.org\",\"User\":\"svc-eda\",\"Password\":\"seed-password\",\"Port\":8443,\"Endpoint\":\"https://eda.example.org/api/v2\"}";

    // What 2.0.0 seeds for GuardConfig: an empty skeleton.
    private const string V2Config = "{\"Host\":\"\",\"User\":\"\",\"Password\":\"\",\"Port\":0,\"Endpoint\":\"\"}";

    public static TheoryData<BlueprintUpdateMode> UpdateModes => new() { BlueprintUpdateMode.Merge, BlueprintUpdateMode.Full };

    /// <summary>
    /// Empty string, empty array, JSON skeleton (the AB#6310 shape: a short empty skeleton over a longer
    /// filled configuration) and omitted attributes over a filled entity: every non-empty tenant value
    /// survives, in Merge and in Full mode, and each blanked attribute is reported with its reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateModes))]
    public async Task ApplyUpdate_EmptyOrOmittedSeedValues_KeepNonEmptyTenantValuesAndReportThem(BlueprintUpdateMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("guard-keep");

        try
        {
            (await service.ApplyBlueprintAsync(tenantId, GuardBpV1, force: false, ct)).IsSuccess.Should().BeTrue();
            await SetTenantTextAsync(tenantId, TenantEditedText);

            var result = await service.ApplyUpdateAsync(tenantId, GuardBpV2, mode, null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            result.EntitiesUpdated.Should().Be(1, "the entity itself is still updated and re-stamped");

            var entity = await GetGuardEntityAsync(tenantId);
            entity.GetAttributeStringValueOrDefault("RtBlueprintSource").Should().Be(GuardBpV2.FullName);
            Show(entity, "GuardText").Should().Be(TenantEditedText, "the seed carries an empty string");
            Show(entity, "GuardConfig").Should().Be(V1Config, "the seed carries an empty JSON skeleton over a filled configuration");
            Show(entity, "GuardTags").Should().Be("a,b", "the seed carries an empty array");
            Show(entity, "GuardCount").Should().Be("4", "the seed omits an attribute without a CK default");
            Show(entity, "GuardLevel").Should().Be("9", "the seed omits an attribute whose stored value differs from its CK default");

            result.BlankedAttributes.Should().OnlyContain(b => !b.AppliedOnUpdate);
            ReasonOf(result.BlankedAttributes, "GuardText").Should().Be("SeedEmpty");
            ReasonOf(result.BlankedAttributes, "GuardConfig").Should().Be("SeedEmpty");
            ReasonOf(result.BlankedAttributes, "GuardTags").Should().Be("SeedEmpty");
            ReasonOf(result.BlankedAttributes, "GuardCount").Should().Be("SeedOmitted");
            ReasonOf(result.BlankedAttributes, "GuardLevel").Should().Be("ResetToDefault");
            var level = result.BlankedAttributes.Single(b => b.AttributeName == "GuardLevel");
            level.IncomingSummary.Should().Be("default (5)");
            result.BlankedAttributes.Select(b => b.RtId).Distinct().Single().Should().Be(entity.RtId.ToString());
            result.BlankedAttributes.Select(b => b.CkTypeId).Distinct().Single().Should().Contain("SeedGuardEntity");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// AB#6395: an omitted attribute whose stored value equals its CK default is written with the same
    /// default again and therefore not reported (GuardMode: stored 3 == default 3), while the omitted
    /// attribute with a different stored value is reported as ResetToDefault and kept (GuardLevel).
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateModes))]
    public async Task ApplyUpdate_OmittedAttributeAtItsDefault_IsNotReported(BlueprintUpdateMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("guard-default");

        try
        {
            (await service.ApplyBlueprintAsync(tenantId, GuardBpV1, force: false, ct)).IsSuccess.Should().BeTrue();

            var preview = await service.PreviewUpdateAsync(tenantId, GuardBpV2, mode, ct);
            var result = await service.ApplyUpdateAsync(tenantId, GuardBpV2, mode, null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            result.BlankedAttributes.Should().NotContain(b => b.AttributeName == "GuardMode",
                "the stored value equals the CK default, the import writes the same default again");
            preview.BlankedAttributes.Should().NotContain(b => b.AttributeName == "GuardMode");

            var entity = await GetGuardEntityAsync(tenantId);
            Show(entity, "GuardMode").Should().Be("3", "the default is materialised again");
            Show(entity, "GuardLevel").Should().Be("9", "the non-default value is kept");
            ReasonOf(result.BlankedAttributes, "GuardLevel").Should().Be("ResetToDefault");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// Only blanking is guarded: a non-empty seed value that differs from the tenant's wins as before
    /// (blueprint-owned change), nothing is reported, and an attribute the seed does not touch keeps
    /// following the seed.
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateModes))]
    public async Task ApplyUpdate_NonEmptyDifferingSeedValues_Win(BlueprintUpdateMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("guard-wins");

        try
        {
            (await service.ApplyBlueprintAsync(tenantId, GuardBpV1, force: false, ct)).IsSuccess.Should().BeTrue();
            await SetTenantTextAsync(tenantId, TenantEditedText);

            var result = await service.ApplyUpdateAsync(tenantId, GuardBpV3, mode, null, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            result.BlankedAttributes.Should().BeEmpty("nothing is blanked, the seed brings non-empty values");

            var entity = await GetGuardEntityAsync(tenantId);
            Show(entity, "GuardText").Should().Be("seed v3");
            Show(entity, "GuardConfig").Should().Contain("eda2.example.org");
            Show(entity, "GuardTags").Should().Be("c");
            Show(entity, "GuardCount").Should().Be("8");
            Show(entity, "GuardLevel").Should().Be("6");
            Show(entity, "GuardMode").Should().Be("4");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// A new entity / initial apply is unchanged by the guard: a tenant without the entity gets the
    /// seed values, also when they are empty.
    /// </summary>
    [Fact]
    public async Task ApplyBlueprint_NewEntity_TakesSeedValuesEvenWhenEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("guard-new");

        try
        {
            (await service.ApplyBlueprintAsync(tenantId, GuardBpV2, force: false, ct)).IsSuccess.Should().BeTrue();

            var entity = await GetGuardEntityAsync(tenantId);
            entity.GetAttributeStringValueOrDefault("RtBlueprintSource").Should().Be(GuardBpV2.FullName);
            Show(entity, "GuardText").Should().BeEmpty();
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// The explicit override: with AllowBlanking the same update blanks the values, and the result still
    /// lists them, now as applied.
    /// </summary>
    [Fact]
    public async Task ApplyUpdate_AllowBlanking_BlanksAndReportsAsApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("guard-allow");

        try
        {
            (await service.ApplyBlueprintAsync(tenantId, GuardBpV1, force: false, ct)).IsSuccess.Should().BeTrue();
            await SetTenantTextAsync(tenantId, TenantEditedText);

            var result = await service.ApplyUpdateAsync(
                tenantId, GuardBpV2, BlueprintUpdateMode.Merge,
                new BlueprintUpdateOptions { AllowBlanking = true }, ct);

            result.Success.Should().BeTrue(string.Join("; ", result.Errors));
            result.BlankedAttributes.Select(b => b.AttributeName).Should().BeEquivalentTo(
                ["GuardText", "GuardConfig", "GuardTags", "GuardCount", "GuardLevel"],
                "exactly the five guarded shapes are reported (GuardMode stays at its default and is not)");
            result.BlankedAttributes.Should().OnlyContain(b => b.AppliedOnUpdate);

            var entity = await GetGuardEntityAsync(tenantId);
            Show(entity, "GuardText").Should().BeEmpty();
            Show(entity, "GuardConfig").Should().Be(V2Config, "the seed's empty JSON skeleton replaces the configuration once confirmed");
            Show(entity, "GuardTags").Should().BeEmpty();
            Show(entity, "GuardCount").Should().BeEmpty("omitted attribute without default is cleared once confirmed");
            Show(entity, "GuardLevel").Should().Be("5", "an omitted attribute with a CK default is reset to the default once confirmed");
            Show(entity, "GuardMode").Should().Be("3");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private static string? ReasonOf(IEnumerable<BlueprintBlankedAttribute> entries, string attribute) =>
        entries.SingleOrDefault(b => string.Equals(b.AttributeName, attribute, StringComparison.OrdinalIgnoreCase))?.Reason;

    /// <summary>Flattens a stored attribute to a comparable string (arrays joined by ',', null as empty).</summary>
    private static string Show(RtEntity entity, string attribute)
    {
        var value = entity.GetAttributeValueOrDefault(attribute);
        return value switch
        {
            null => string.Empty,
            string s => s,
            IEnumerable e => string.Join(",", e.Cast<object?>().Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture))),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private async Task<RtEntity> GetGuardEntityAsync(string tenantId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var set = await repository.GetRtEntitiesByTypeAsync(session, GuardCkType, RtEntityQueryOptions.Create());
        return set.Items.Should().ContainSingle().Subject;
    }

    /// <summary>Simulates an operator editing the seed-owned text on the tenant.</summary>
    private async Task SetTenantTextAsync(string tenantId, string text)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var set = await repository.GetRtEntitiesByTypeAsync(session, GuardCkType, RtEntityQueryOptions.Create());
        var target = set.Items.Should().ContainSingle().Subject;
        target.SetAttributeRawValue("GuardText", text);

        session.StartTransaction();
        await repository.ReplaceOneRtEntityByIdAsync(session, GuardCkType, target.RtId, target);
        await session.CommitTransactionAsync();
    }
}
