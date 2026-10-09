using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 Phase 0 (contract §3.4, Test 2): the runtime CK cache — rebuilt from MongoDB, not from the
///     compiled catalog — carries the v2 members of the kitchen-sink model. Template:
///     <see cref="CkAttributeOwnershipPersistenceTests" />. The JSON gate (<see cref="CkMetaModelRoundTripTests" />)
///     proves the DTO read-back; this proves the read-back reaches the graph consumers use
///     (<see cref="ICkCacheService" />: GraphQL access filtering, method dispatch, interface types).
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkMetaModelCacheRoundTripTests(CkModelImportMigrationFixture fixture)
{
    [Fact]
    public async Task CkV2KitchenSink_CacheRebuiltFromMongo_CarriesTheDeclaredV2Members()
    {
        var helper = new CkMetaModelRoundTripTests(fixture);
        await helper.WithThrowawayTenantAsync("rtv2cache", async (tenant, tenantId) =>
        {
            // The MSBuild-compiled YAML kitchen sink: imported through the catalog like a real model.
            var modelId = CkMetaModelRoundTripTests.YamlKitchenSinkModelId;
            var operationResult = new OperationResult();
            await tenant.ImportCkModelAsync(modelId, operationResult);
            Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));

            var cacheService = fixture.GetService<ICkCacheService>();
            if (cacheService.IsTenantLoaded(tenantId))
            {
                cacheService.Unload(tenantId);
            }

            await tenant.LoadCacheForTenantAsync();
            Assert.True(cacheService.IsTenantLoaded(tenantId));

            // Interfaces (AB#5667)
            var named = cacheService.GetRtCkInterface(tenantId, new RtCkId<CkInterfaceId>("KitchenSink/Named"));
            Assert.Equal("things with a name", named.Description);
            Assert.Equal(["Alias:True", "Name:False"],
                named.DefinedAttributes.Select(a => $"{a.AttributeName}:{a.IsOptional}").Order(StringComparer.Ordinal));
            Assert.Contains(cacheService.GetRtCkInterfaces(tenantId),
                i => i.CkInterfaceId.ToRtCkId() == new RtCkId<CkInterfaceId>("KitchenSink/Coded"));

            // Declared implements (AB#5667)
            var gadget = cacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>("KitchenSink/Gadget"));
            Assert.Equal([new CkId<CkInterfaceId>(modelId, new CkInterfaceId("Coded-1"))], gadget.DeclaredImplements);
            var thing = cacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>("KitchenSink/Thing"));
            Assert.Equal([new CkId<CkInterfaceId>(modelId, new CkInterfaceId("Named-1"))], thing.DeclaredImplements);

            // Inherited views, resolved by the engine graph from the read-back (only DECLARED rows are stored)
            Assert.Equal(["Coded-1", "Named-1"],
                gadget.AllImplementedInterfaces.Select(i => i.ElementId.FullName).Order(StringComparer.Ordinal));
            Assert.Equal(["ChangePassword-2", "Ping-1", "Reindex-1"], gadget.AllMethods.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("KitchenSink/Thing.ChangePassword-2", gadget.AllMethods["ChangePassword-2"].QualifiedMethodId);
            Assert.Equal(["Gadget", "Thing", "Widget"],
                named.ImplementingTypes.Select(t => t.ElementId.Name).Order(StringComparer.Ordinal));
            var widgetGraph = cacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>("KitchenSink/Widget"));
            Assert.Empty(widgetGraph.AllMethods);
            Assert.Equal(["Named-1"], widgetGraph.AllImplementedInterfaces.Select(i => i.ElementId.FullName));

            // Phase 1 members (F1.3-S2) reach the cache rebuilt from Mongo.
            Assert.Equal(CkDerivableDto.Model, thing.Derivable);
            Assert.Equal(CkVisibilityDto.Internal, cacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>("KitchenSink/Widget")).Visibility);
            var labeled = cacheService.GetRtCkInterface(tenantId, new RtCkId<CkInterfaceId>("KitchenSink/Labeled"));
            Assert.Equal(["KitchenSink/Named-1"], labeled.DeclaredExtends.Select(i => i.ToRtCkId().FullName));
            Assert.Single(labeled.DefinedAssociations);
            Assert.Equal(["Relabel-1"], labeled.DefinedMethods.Select(m => m.MethodId));
            Assert.True(cacheService.GetRtCkInterface(tenantId, new RtCkId<CkInterfaceId>("KitchenSink/Legacy")).Deprecated);
            Assert.Equal(CkVisibilityDto.Internal,
                cacheService.GetRtCkInterface(tenantId, new RtCkId<CkInterfaceId>("KitchenSink/Coded")).Visibility);

            // Declared methods (AB#5669), field by field
            Assert.Equal(["ChangePassword-2", "Ping-1", "Reindex-1"],
                thing.DefinedMethods.Select(m => m.MethodId).Order(StringComparer.Ordinal));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(RoundTripMethods.Full(modelId)),
                System.Text.Json.JsonSerializer.Serialize(thing.DefinedMethods.Single(m => m.MethodId == "ChangePassword-2")));
            Assert.Equal(CkMethodExecutionDto.DefaultTimeoutSeconds, thing.AllMethods["Ping-1"].TimeoutSeconds);

            // Access (AB#5668) at all three assignment read-back sites: type, record, association role
            Assert.Equal(CkAttributeAccessDto.Hidden, thing.AllAttributesByName["Secret"].Access);
            Assert.Equal(CkAttributeAccessDto.ReadOnly, thing.AllAttributesByName["Alias"].Access);
            Assert.Equal(CkAttributeAccessDto.MethodOnly, gadget.AllAttributesByName["Code"].Access);
            var widget = cacheService.GetRtCkType(tenantId, new RtCkId<CkTypeId>("KitchenSink/Widget"));
            Assert.Equal(CkAttributeAccessDto.ReadWrite, widget.AllAttributesByName["Name"].Access);

            var address = cacheService.GetRtCkRecord(tenantId, new RtCkId<CkRecordId>("KitchenSink/Address"));
            Assert.Equal(CkAttributeAccessDto.ReadWrite, address.AllAttributesByName["Name"].Access);
            Assert.Equal(CkAttributeAccessDto.ReadOnly, address.AllAttributesByName["Alias"].Access);
            Assert.Equal(CkAttributeAccessDto.MethodOnly, address.AllAttributesByName["Code"].Access);
            Assert.Equal(CkAttributeAccessDto.Hidden, address.AllAttributesByName["Secret"].Access);

            var link = cacheService.GetRtCkAssociationRole(tenantId, new RtCkId<CkAssociationRoleId>("KitchenSink/Link"));
            Assert.Equal(CkAttributeAccessDto.ReadOnly, link.AllAttributesByName["Code"].Access);
            Assert.Equal(CkAttributeAccessDto.MethodOnly, link.AllAttributesByName["Mode"].Access);
            Assert.Equal(CkAttributeAccessDto.ReadOnly, link.AllAttributesByName["Secret"].Access);

            // CK language (model level): CkCacheRoot.Models carries it, read through the persisted cache file.
            using var stream = new MemoryStream();
            await cacheService.SaveCacheAsync(tenantId, stream);
            var cacheRoot = System.Text.Json.Nodes.JsonNode.Parse(stream.ToArray())!.AsObject();
            var models = cacheRoot.First(p => string.Equals(p.Key, "models", StringComparison.OrdinalIgnoreCase))
                .Value!.AsArray();
            var kitchenSink = models.Single(m => m!.AsObject().Any(p =>
                string.Equals(p.Key, "modelId", StringComparison.OrdinalIgnoreCase) &&
                p.Value!.ToString() == modelId.FullName))!.AsObject();
            Assert.Equal(2, kitchenSink.First(p => string.Equals(p.Key, "ckLanguage",
                StringComparison.OrdinalIgnoreCase)).Value!.GetValue<int>());
        });
    }
}
