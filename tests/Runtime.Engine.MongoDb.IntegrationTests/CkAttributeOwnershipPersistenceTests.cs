using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// AB#5187. Attribute <c>ownership</c> — the four-valued replacement for the <c>isRuntimeState</c>
/// boolean — must survive the full MongoDB persistence round-trip: <c>CkAttributeDto</c> /
/// <c>CkTypeAttributeDto</c> → <c>CkAttribute</c> / <c>CkTypeAttribute</c> entity on import
/// (<c>DatabaseCkModelRepository.ProcessCkAttributes</c> / <c>ProcessCkTypeAttributes</c>) and back
/// on read-back (<c>TryLookupCkModelAsync</c>), so the runtime CK cache — which is rebuilt from
/// MongoDB and not from the compiled catalog — resolves the value the model actually declares.
/// <para>
/// Same failure mode as AB#4589 (<see cref="CkAttributeRuntimeStatePersistenceTests" />), but
/// quieter: without the mapping the persisted boolean mirror still resolves every attribute to
/// <c>RuntimeState</c> or <c>SeedOwned</c>, so nothing throws and nothing looks broken — a
/// <c>TenantOwned</c> attribute simply behaves like <c>RuntimeState</c> and stays excluded from
/// <c>ExportRt</c>, which is the exact gap the feature exists to close. The per-assignment override
/// has no boolean fallback at all and would vanish outright.
/// </para>
/// <para>
/// The Test model declares <c>TariffRate</c> (TenantOwned), <c>ApiKey</c> (Secret) and
/// <c>SyncCursor</c> (RuntimeState), assigns all three unchanged on <c>Test/OwnershipEntity</c>,
/// and overrides them per assignment on <c>Test/OwnershipOverrideEntity</c>, the
/// <c>Test/OwnershipRecord</c> record and the <c>Test/OwnershipLink</c> association role — the
/// three distinct assignment read sites in <c>DatabaseCkModelRepository</c>.
/// </para>
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkAttributeOwnershipPersistenceTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly RtCkId<CkTypeId> OwnershipEntityTypeId = new("Test/OwnershipEntity");
    private static readonly RtCkId<CkTypeId> OwnershipOverrideEntityTypeId = new("Test/OwnershipOverrideEntity");
    private static readonly RtCkId<CkRecordId> OwnershipRecordId = new("Test/OwnershipRecord");
    private static readonly RtCkId<CkAssociationRoleId> OwnershipLinkRoleId = new("Test/OwnershipLink");
    private static readonly RtCkId<CkAttributeId> ApiKeyAttributeId = new("Test/ApiKey");
    private static readonly RtCkId<CkAttributeId> TariffRateAttributeId = new("Test/TariffRate");
    private static readonly RtCkId<CkAttributeId> SyncCursorAttributeId = new("Test/SyncCursor");
    private static readonly RtCkId<CkAttributeId> NameAttributeId = new("Test/Name");

    [Fact]
    public async Task ImportedModel_AttributeOwnership_SurvivesMongoRoundTripIntoCache()
    {
        var cacheService = await ImportAndReloadCacheAsync();
        var tenantId = fixture.GetSystemContext().TenantId;

        // --- Attribute DEFINITIONS -------------------------------------------------------
        // Each declared value must arrive verbatim. Collapsing onto the boolean mirror would
        // turn TenantOwned and Secret into RuntimeState — indistinguishable at runtime, but the
        // difference decides whether the value is exported (TenantOwned) or not (Secret).
        Assert.Equal(AttributeOwnershipDto.TenantOwned,
            cacheService.GetRtCkAttribute(tenantId, TariffRateAttributeId).Ownership);
        Assert.Equal(AttributeOwnershipDto.Secret,
            cacheService.GetRtCkAttribute(tenantId, ApiKeyAttributeId).Ownership);
        Assert.Equal(AttributeOwnershipDto.RuntimeState,
            cacheService.GetRtCkAttribute(tenantId, SyncCursorAttributeId).Ownership);

        // Control: an attribute with no marker at all stays SeedOwned — proving we read the
        // persisted value rather than a blanket non-default.
        Assert.Equal(AttributeOwnershipDto.SeedOwned,
            cacheService.GetRtCkAttribute(tenantId, NameAttributeId).Ownership);

        // --- Attribute ASSIGNMENTS without an override ------------------------------------
        var ownershipEntity = cacheService.GetRtCkType(tenantId, OwnershipEntityTypeId);
        Assert.Equal(AttributeOwnershipDto.TenantOwned, EffectiveOwnership(ownershipEntity, "TariffRate"));
        Assert.Equal(AttributeOwnershipDto.Secret, EffectiveOwnership(ownershipEntity, "ApiKey"));
        Assert.Equal(AttributeOwnershipDto.RuntimeState, EffectiveOwnership(ownershipEntity, "SyncCursor"));
        Assert.Equal(AttributeOwnershipDto.SeedOwned, EffectiveOwnership(ownershipEntity, "Name"));

        // The deprecated mirror keeps meaning "preserved on Upsert" for all three non-default
        // values, which is what an engine that predates AB#5187 relies on.
        Assert.True(ownershipEntity.AllAttributesByName["TariffRate"].IsRuntimeState);
        Assert.True(ownershipEntity.AllAttributesByName["ApiKey"].IsRuntimeState);
        Assert.False(ownershipEntity.AllAttributesByName["Name"].IsRuntimeState);
    }

    /// <summary>
    /// The per-assignment override is the part with NO legacy fallback: <c>CkTypeAttribute</c> never
    /// had an ownership or runtime-state member, so an unmapped override does not degrade — it
    /// disappears, silently reverting every overridden assignment to its definition's value.
    /// One shared definition is deliberately overridden in BOTH directions here.
    /// </summary>
    [Fact]
    public async Task ImportedModel_PerAssignmentOwnershipOverride_SurvivesMongoRoundTripIntoCache()
    {
        var cacheService = await ImportAndReloadCacheAsync();
        var tenantId = fixture.GetSystemContext().TenantId;

        // Type attribute: the shared ApiKey definition is Secret, this assignment narrows it to
        // SeedOwned; Name carries no marker at all and this assignment widens it to TenantOwned.
        var overrideEntity = cacheService.GetRtCkType(tenantId, OwnershipOverrideEntityTypeId);
        Assert.Equal(AttributeOwnershipDto.SeedOwned, EffectiveOwnership(overrideEntity, "ApiKey"));
        Assert.Equal(AttributeOwnershipDto.TenantOwned, EffectiveOwnership(overrideEntity, "Name"));

        // The mirror follows the OVERRIDE, not the definition: a SeedOwned assignment of a Secret
        // definition is overwritten on re-apply.
        Assert.False(overrideEntity.AllAttributesByName["ApiKey"].IsRuntimeState);
        Assert.True(overrideEntity.AllAttributesByName["Name"].IsRuntimeState);

        // The definition itself is untouched by the override — the same attribute is still Secret
        // where it is assigned unchanged.
        Assert.Equal(AttributeOwnershipDto.Secret,
            cacheService.GetRtCkAttribute(tenantId, ApiKeyAttributeId).Ownership);
        Assert.Equal(AttributeOwnershipDto.Secret,
            EffectiveOwnership(cacheService.GetRtCkType(tenantId, OwnershipEntityTypeId), "ApiKey"));

        // Record attribute assignment — second of the three read sites.
        var record = cacheService.GetRtCkRecord(tenantId, OwnershipRecordId);
        Assert.Equal(AttributeOwnershipDto.SeedOwned, EffectiveOwnership(record, "ApiKey"));
        Assert.Equal(AttributeOwnershipDto.TenantOwned, EffectiveOwnership(record, "TariffRate"));

        // Association-role attribute assignment — third read site.
        var role = cacheService.GetRtCkAssociationRole(tenantId, OwnershipLinkRoleId);
        Assert.Equal(AttributeOwnershipDto.SeedOwned, EffectiveOwnership(role, "ApiKey"));
        Assert.Equal(AttributeOwnershipDto.RuntimeState, EffectiveOwnership(role, "SyncCursor"));
    }

    private static AttributeOwnershipDto EffectiveOwnership(CkTypeWithAttributesGraph graph, string attributeName)
    {
        return graph.AllAttributesByName[attributeName].Ownership;
    }

    private async Task<ICkCacheService> ImportAndReloadCacheAsync()
    {
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();
        var cacheService = fixture.GetService<ICkCacheService>();
        var tenantId = systemContext.TenantId;

        if (cacheService.IsTenantLoaded(tenantId))
        {
            cacheService.Unload(tenantId);
        }

        // Import persists the CkAttribute / CkTypeAttribute documents (DTO -> entity mapping).
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);

        // Rebuild the cache FROM MongoDB — the read-back path (entity -> DTO -> graph) that a
        // missing mapping breaks. Asserting against the compiled catalog instead would stay green.
        await systemContext.LoadCacheForTenantAsync();
        Assert.True(cacheService.IsTenantLoaded(tenantId));

        return cacheService;
    }
}
