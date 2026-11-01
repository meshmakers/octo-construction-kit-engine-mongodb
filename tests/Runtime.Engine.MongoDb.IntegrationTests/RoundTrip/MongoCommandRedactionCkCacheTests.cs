using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     AB#8009: the sensitive attribute names of the MongoDB command redaction come from the CK cache rebuilt from
///     MongoDB. The kitchen sink declares <c>Thing.Secret</c> and <c>Address.Secret</c> as <c>access: Hidden</c> and the
///     attribute <c>Alias</c> as <c>securitySensitive</c>.
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class MongoCommandRedactionCkCacheTests(CkModelImportMigrationFixture fixture)
{
    private const string DummyHidden = "DUMMY-HIDDEN-VALUE-8009";
    private const string DummyAlias = "DUMMY-ALIAS-VALUE-8009";

    [Fact]
    public async Task KitchenSink_HiddenAndSensitiveAttributes_AreMaskedInTheRenderedCommand()
    {
        var helper = new CkMetaModelRoundTripTests(fixture);
        await helper.WithThrowawayTenantAsync("rtredact", async (tenant, tenantId) =>
        {
            var operationResult = new OperationResult();
            await tenant.ImportCkModelAsync(CkMetaModelRoundTripTests.YamlKitchenSinkModelId, operationResult);
            Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));

            var cacheService = fixture.GetService<ICkCacheService>();
            if (cacheService.IsTenantLoaded(tenantId))
            {
                cacheService.Unload(tenantId);
            }

            await tenant.LoadCacheForTenantAsync();

            var names = MongoCommandSensitiveAttributeNames.CollectFromCkCache(cacheService, tenantId);
            Assert.NotNull(names);
            Assert.Contains("Secret", names);
            Assert.Contains("Alias", names);
            Assert.DoesNotContain("Name", names);

            // The listener built with the real cache renders a write of the tenant without the values.
            var observability = new MongoCommandObservability(NullLogger<MongoCommandObservability>.Instance,
                new StaticOptionsMonitor(new OctoSystemConfiguration()), ckCacheService: cacheService);
            var command = new BsonDocument
            {
                { "insert", "KitchenSink_Thing" },
                {
                    "documents", new BsonArray
                    {
                        new BsonDocument
                        {
                            { "ckTypeId", "KitchenSink/Gadget" },
                            {
                                "attributes", new BsonDocument
                                {
                                    { "name", "visible-name" },
                                    { "alias", DummyAlias },
                                    { "secret", DummyHidden }
                                }
                            }
                        }
                    }
                }
            };
            var connectionId = new ConnectionId(new ServerId(new ClusterId(1),
                new System.Net.DnsEndPoint("localhost", 27017)));
            var started = new CommandStartedEvent("insert", command, new DatabaseNamespace(tenantId),
                operationId: 1, requestId: 1, connectionId);

            var rendered = observability.RenderCommandForLog(started);

            Assert.DoesNotContain(DummyHidden, rendered);
            Assert.DoesNotContain(DummyAlias, rendered);
            Assert.Contains("\"secret\" : \"***\"", rendered);
            Assert.Contains("\"name\" : \"visible-name\"", rendered);
        });
    }

    private sealed class StaticOptionsMonitor(OctoSystemConfiguration value) : IOptionsMonitor<OctoSystemConfiguration>
    {
        public OctoSystemConfiguration CurrentValue => value;

        public OctoSystemConfiguration Get(string? name) => value;

        public IDisposable? OnChange(Action<OctoSystemConfiguration, string?> listener) => null;
    }
}
