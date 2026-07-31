using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Regression coverage for AB#4589. The <c>isRuntimeState</c> CK-attribute flag must survive the
/// full MongoDB persistence round-trip — <c>CkAttributeDto</c> → <c>CkAttribute</c> entity on
/// import (<c>DatabaseCkModelRepository.ProcessCkAttributes</c>) and back to <c>CkAttributeDto</c>
/// on read-back (<c>TryLookupCkModelAsync</c>) — so the runtime CK cache, which is rebuilt from
/// MongoDB rather than the compiled catalog JSON, reports it correctly.
/// <para>
/// Before the fix the persistence layer dropped the flag (the entity had no property and neither
/// mapping copied it), the cache always read <c>false</c>, and runtime-state preservation
/// (<c>ImportRtModelCommand.PreserveRuntimeStateAttributesAsync</c>, AB#4582/AB#4589) silently
/// never fired — despite green unit tests that build the graph directly. <c>DataCount</c> on the
/// Test model's <c>BinaryEntity</c> is flagged <c>isRuntimeState: true</c> for this test.
/// </para>
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkAttributeRuntimeStatePersistenceTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly RtCkId<CkTypeId> BinaryEntityTypeId = new("Test/BinaryEntity");

    [Fact]
    public async Task ImportedAttribute_RuntimeStateFlag_SurvivesMongoRoundTripIntoCache()
    {
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();
        var cacheService = fixture.GetService<ICkCacheService>();
        var tenantId = systemContext.TenantId;

        if (cacheService.IsTenantLoaded(tenantId))
        {
            cacheService.Unload(tenantId);
        }

        // Import persists CkAttribute documents to MongoDB (DTO -> entity mapping).
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);

        // Rebuild the cache from MongoDB — this is the read-back path (entity -> DTO -> graph)
        // that the AB#4589 fix touches; a dropped flag would surface here as IsRuntimeState=false.
        await systemContext.LoadCacheForTenantAsync();
        Assert.True(cacheService.IsTenantLoaded(tenantId));

        var typeGraph = cacheService.GetRtCkType(tenantId, BinaryEntityTypeId);
        Assert.NotNull(typeGraph);

        // DataCount is flagged isRuntimeState: true in the Test model.
        var dataCount = typeGraph.AllAttributes.Values.Single(a => a.AttributeName == "DataCount");
        Assert.True(dataCount.IsRuntimeState,
            "isRuntimeState must survive the MongoDB persistence round-trip into the runtime cache (AB#4589)");

        // Binary is not flagged — the control proving we read the real persisted value,
        // not a blanket true.
        var binary = typeGraph.AllAttributes.Values.Single(a => a.AttributeName == "Binary");
        Assert.False(binary.IsRuntimeState);
    }
}
