using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5533: <c>CkRecordDto.RecordKey</c> (AB#5528 concept §4.6) must survive the MongoDB
///     persistence round-trip - <c>CkRecordDto</c> → <c>CkRecord</c> document on import
///     (<c>DatabaseCkModelRepository.ProcessCkRecords</c>) and back on read-back
///     (<c>TryLookupCkModelAsync</c>) - because the runtime CK cache is rebuilt from MongoDB. A dropped
///     key would make the cache read <c>null</c> and break the per-element carry-over of Secret
///     record sub-values (same failure class as AB#4589 for <c>isRuntimeState</c>).
///     <c>Test/CredentialEntry</c> declares <c>recordKey: Key</c>; <c>Test/EMailAddress</c> declares none.
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkRecordKeyPersistenceTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly RtCkId<CkTypeId> SecretHolderTypeId = new("Test/SecretHolder");
    private static readonly RtCkId<CkTypeId> CustomerTypeId = new("Test/Customer");

    [Fact]
    public async Task ImportedRecord_RecordKey_SurvivesMongoRoundTripIntoCache()
    {
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();
        var cacheService = fixture.GetService<ICkCacheService>();
        var tenantId = systemContext.TenantId;

        if (cacheService.IsTenantLoaded(tenantId))
        {
            cacheService.Unload(tenantId);
        }

        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        Assert.False(operationResult.HasErrors);

        // Rebuild the cache from MongoDB - the read-back path the fix touches.
        await systemContext.LoadCacheForTenantAsync();
        Assert.True(cacheService.IsTenantLoaded(tenantId));

        var holder = cacheService.GetRtCkType(tenantId, SecretHolderTypeId);
        var credentialRecordId = holder.AllAttributesByName["Credentials"].ValueCkRecordId;
        Assert.NotNull(credentialRecordId);
        var credentialRecord = cacheService.GetCkRecord(tenantId, credentialRecordId!);
        Assert.Equal("Key", credentialRecord.RecordKey);

        // Control: a record without a declared key reads back as null, not a blanket value.
        var customer = cacheService.GetRtCkType(tenantId, CustomerTypeId);
        var mailRecordId = customer.AllAttributesByName["EMailAddresses"].ValueCkRecordId;
        Assert.NotNull(mailRecordId);
        Assert.Null(cacheService.GetCkRecord(tenantId, mailRecordId!).RecordKey);
    }
}
