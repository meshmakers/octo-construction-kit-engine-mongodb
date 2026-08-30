using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// AB#4978. The <c>ownerAttributePath</c> CK-type declaration must survive the full MongoDB
/// persistence round-trip — <c>CkTypeDto</c> → <c>CkType</c> entity on import
/// (<c>DatabaseCkModelRepository</c>) and back to <c>CkCompiledTypeDto</c> on read-back
/// (<c>TryLookupCkModelAsync</c>) — so the runtime CK cache, which is rebuilt from MongoDB rather
/// than the compiled catalog JSON, resolves the effective owner attribute (including inheritance
/// to derived types) for the owned-only data-permission predicate. Same coverage pattern as
/// <see cref="CkAttributeRuntimeStatePersistenceTests" /> (AB#4589), whose gap is how that flag
/// originally shipped broken. <c>Test/Ticket</c> declares <c>ownerAttributePath: AssigneeId</c>;
/// <c>Test/EscalationTicket</c> inherits it.
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkTypeOwnerAttributePersistenceTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly RtCkId<CkTypeId> TicketTypeId = new("Test/Ticket");
    private static readonly RtCkId<CkTypeId> EscalationTicketTypeId = new("Test/EscalationTicket");
    private static readonly RtCkId<CkTypeId> ContinentTypeId = new("Test/Continent");

    [Fact]
    public async Task ImportedType_OwnerAttributePath_SurvivesMongoRoundTripIntoCache()
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

        await systemContext.LoadCacheForTenantAsync();
        Assert.True(cacheService.IsTenantLoaded(tenantId));

        var ticketGraph = cacheService.GetRtCkType(tenantId, TicketTypeId);
        Assert.Equal("AssigneeId", ticketGraph.OwnerAttributePath);

        // Inheritance is resolved when the graph is rebuilt from the persisted (declaring-type-only)
        // value — the derived type carries the effective owner attribute.
        var escalationGraph = cacheService.GetRtCkType(tenantId, EscalationTicketTypeId);
        Assert.Equal("AssigneeId", escalationGraph.OwnerAttributePath);

        // Record paths survive verbatim (Test/ReviewTask declares Owner.UserId).
        var reviewTaskGraph = cacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>("Test/ReviewTask"));
        Assert.Equal("Owner.UserId", reviewTaskGraph.OwnerAttributePath);

        // Control: a type without a declaration stays on the rtCreatedBy default.
        var continentGraph = cacheService.GetRtCkType(tenantId, ContinentTypeId);
        Assert.Null(continentGraph.OwnerAttributePath);
    }
}
