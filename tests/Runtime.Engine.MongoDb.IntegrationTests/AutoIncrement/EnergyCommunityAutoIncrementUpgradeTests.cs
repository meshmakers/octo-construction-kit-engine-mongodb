using System.Text;
using System.Text.Json.Nodes;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     AB#9756 (Feature AB#8675 AC 6): existing energy-community document and customer numbers survive the upgrade of a
///     tenant from System 2.8.0 to the System of this engine (2.9.0, the number-range schema of AB#9752) unchanged.
///     <para>
///         The fixture is a copy, never live data: the compiled CK models Basic-2.7.0, Basic.Energy-2.1.0 and
///         EnergyCommunity-5.1.0 as the compiler built them against System 2.8.0, and the two counters of the
///         EnergyCommunity.Base seed (<c>testData/energyCommunityUpgrade</c>, sources in its README). The System 2.8.0
///         of the tenant is the embedded System without the elements System 2.9.0 added (<see cref="System280Async" />);
///         the upgrade is the one every service runs: the tenant resolve imports the embedded, newer System
///         (<c>UpdateSystemCkModelAsync</c>).
///     </para>
///     Compatibility result: no CK migration script is needed - 2.9.0 only adds optional attributes, records, enums, a
///     type and an association role, and a counter without them numbers exactly as before (floor 0, AB#9755).
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class EnergyCommunityAutoIncrementUpgradeTests(CkModelImportGuardFixture fixture)
{
    private const string TestDataFolder = "testData/energyCommunityUpgrade";
    private const string CounterCollection = "RtEntity_SystemAutoIncrement";
    private const string RtCollectionPrefix = "RtEntity_";
    private const string SystemRtCollectionPrefix = "RtEntity_System";

    private static readonly OctoObjectId DocumentCounterId = new("68480f3ef579d8c26f35ad47");
    private static readonly OctoObjectId CustomerCounterId = new("686ad5878ae68659b20571d0");
    private static readonly CkModelId System280 = new("System-2.8.0");

    private static readonly RtCkId<CkTypeId> BillingDocumentType = new("EnergyCommunity/BillingDocument");
    private static readonly RtCkId<CkTypeId> CustomerType = new("EnergyCommunity/Customer");

    /// <summary>The elements System 2.9.0 added (AB#9752), by their base names (without model and version).</summary>
    private static readonly string[] System290AutoIncrementAttributes =
        ["StartValue", "PeriodKind", "FiscalYearStartMonth", "ReferenceTimeZone", "Periods", "Release"];

    private static readonly string[] System290Markers =
    [
        "AutoIncrement.StartValue", "AutoIncrement.PeriodKind", "AutoIncrement.FiscalYearStartMonth",
        "AutoIncrement.ReferenceTimeZone", "AutoIncrement.Periods", "AutoIncrement.Release", "AutoIncrementPeriod",
        "NumberRangeRelease", "DocumentAudit", "Cancels"
    ];

    /// <summary>
    ///     AC 1: every DocumentNumber and CustomerNumber (and both counters) is byte-identical after the upgrade, the
    ///     upgrade really ran from 2.8.0 to the embedded System, and the energy-community models stay available.
    /// </summary>
    [Fact]
    public async Task Upgrade_KeepsEveryDocumentAndCustomerNumber()
    {
        await WithUpgradedTenantAsync("ecupkeep", (_, _, existing) =>
        {
            Assert.Equal(["0001", "0002", "0003", "0004"], existing.DocumentNumbers);
            Assert.Equal(["1", "2", "3", "10001", "10002"], existing.CustomerNumbers);
            Assert.Equal(existing.Before, existing.After);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    ///     AC 2: after the upgrade the next billing document gets <c>CurrentValue + 1</c> in the legacy <c>{0:D4}</c>
    ///     format, the next customer the next plain number; the counters keep the 2.8.0 shape (no StartValue, no
    ///     periods written).
    /// </summary>
    [Fact]
    public async Task AfterTheUpgrade_TheNextDocumentAndCustomer_ContinueTheCounter()
    {
        await WithUpgradedTenantAsync("ecupnext", async (repository, database, _) =>
        {
            Assert.Equal(4, await CounterValueAsync(database, DocumentCounterId));
            Assert.Equal(3, await CounterValueAsync(database, CustomerCounterId));

            var document = BillingDocument();
            var customer = Customer();
            await ApplyAsync(repository, Insert(document), Insert(customer));

            Assert.Equal("0005", Number(document, "DocumentNumber"));
            Assert.Equal("4", Number(customer, "CustomerNumber"));
            Assert.Equal(5, await CounterValueAsync(database, DocumentCounterId));
            Assert.Equal(4, await CounterValueAsync(database, CustomerCounterId));
            foreach (var counterId in new[] { DocumentCounterId, CustomerCounterId })
            {
                var attributes = (await FindAsync(database, CounterCollection, counterId))["attributes"].AsBsonDocument;
                Assert.DoesNotContain(attributes.Names, n => n is "startValue" or "periodKind" or "periods");
            }
        });
    }

    /// <summary>
    ///     AC 3: a replace without the number and partial updates of other attributes keep the stored number and leave
    ///     the counters alone; the next document still continues the counter.
    /// </summary>
    [Fact]
    public async Task AfterTheUpgrade_ReplaceAndPartialUpdate_KeepTheNumberAndTheCounter()
    {
        await WithUpgradedTenantAsync("ecupupd", async (repository, database, existing) =>
        {
            var replaced = existing.Documents[0];
            var replacement = BillingDocument(replaced.RtId, grossTotal: 99.5);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateReplace(replacement.ToRtEntityId(), replacement));

            var updated = existing.Documents[1];
            var released = new RtEntity(BillingDocumentType, updated.RtId,
                new Dictionary<string, object?> { ["BillingDocumentState"] = 1 });
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(released.ToRtEntityId(), released));

            var customer = existing.Customers[0];
            var renamed = new RtEntity(CustomerType, customer.RtId,
                new Dictionary<string, object?> { ["Comment"] = "moved" });
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(renamed.ToRtEntityId(), renamed));

            Assert.Equal("0001", await StoredNumberAsync(database, replaced.RtId, "documentNumber"));
            Assert.Equal(99.5, (await FindRtAsync(database, replaced.RtId))["attributes"]["grossTotal"].ToDouble());
            Assert.Equal("0002", await StoredNumberAsync(database, updated.RtId, "documentNumber"));
            Assert.Equal(1, (await FindRtAsync(database, updated.RtId))["attributes"]["billingDocumentState"].ToInt32());
            Assert.Equal("1", await StoredNumberAsync(database, customer.RtId, "customerNumber"));
            Assert.Equal(4, await CounterValueAsync(database, DocumentCounterId));
            Assert.Equal(3, await CounterValueAsync(database, CustomerCounterId));

            var next = BillingDocument();
            await ApplyAsync(repository, Insert(next));
            Assert.Equal("0005", Number(next, "DocumentNumber"));
        });
    }

    /// <summary>
    ///     AC 4: an ImportRt of exported documents and customers that carry numbers (new ones, and existing ones again)
    ///     keeps the imported numbers and leaves both counters unchanged.
    /// </summary>
    [Fact]
    public async Task AfterTheUpgrade_ImportRt_KeepsTheImportedNumbers_AndTheCounter()
    {
        await WithUpgradedTenantAsync("ecupimp", async (repository, database, existing) =>
        {
            var importedDocumentId = OctoObjectId.GenerateNewId();
            var importedCustomerId = OctoObjectId.GenerateNewId();
            var reimported = existing.Documents[2];
            await ImportRtAsync(repository, ExportYaml(
                (BillingDocumentType, importedDocumentId, "DocumentNumber", "0042"),
                (BillingDocumentType, reimported.RtId, "DocumentNumber", "0003"),
                (CustomerType, importedCustomerId, "CustomerNumber", "0815")));

            Assert.Equal("0042", await StoredNumberAsync(database, importedDocumentId, "documentNumber"));
            Assert.Equal("0003", await StoredNumberAsync(database, reimported.RtId, "documentNumber"));
            Assert.Equal("0815", await StoredNumberAsync(database, importedCustomerId, "customerNumber"));
            Assert.Equal(4, await CounterValueAsync(database, DocumentCounterId));
            Assert.Equal(3, await CounterValueAsync(database, CustomerCounterId));
        });
    }

    /// <summary>
    ///     AC 5: the counter seeds of the demo data (energy-community-deployment and pipeline-editor) still import into a
    ///     tenant on the embedded System, and numbering continues from the current value they carry, in their own
    ///     format. The pipeline-editor seed used the CK v1 ids <c>System-1.0.0/AutoIncrement-1.0.0</c>, which
    ///     <c>CkTypeId</c> refuses whatever the System version (run b320); it was moved to the current ids with AB#9756
    ///     (pipeline-editor 0032172), and the copy here is that fixed file.
    /// </summary>
    [Theory]
    [InlineData("energy-community-deployment-rt-autoincrement.yaml")]
    [InlineData("pipeline-editor-rt-autoincrement.yaml")]
    public async Task DemoCounterSeeds_ImportOnTheEmbeddedSystem_AndNumberingContinues(string seedFile)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"ecupdemo{Guid.NewGuid():N}"[..20];
        try
        {
            var tenant = await CreateChildTenantAsync(systemContext, tenantId);
            foreach (var file in new[] { "ck-basic-2.yaml", "ck-basic.energy-2.yaml", "ck-energycommunity-5.yaml" })
            {
                await tenant.ImportCkModelAsync(await CompiledModelAsync(file));
            }

            var repository = tenant.GetTenantRepository();
            await ImportRtAsync(repository, await File.ReadAllTextAsync(Path.Combine(TestDataFolder, seedFile),
                TestContext.Current.CancellationToken));
            var database = GetTenantDatabase(tenant.DatabaseName);
            Assert.Equal(0, await CounterValueAsync(database, DocumentCounterId));
            Assert.Equal(274, await CounterValueAsync(database, CustomerCounterId));

            var document = BillingDocument();
            var customer = Customer();
            await ApplyAsync(repository, Insert(document), Insert(customer));

            Assert.Equal("25-0001", Number(document, "DocumentNumber"));
            Assert.Equal("275", Number(customer, "CustomerNumber"));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     The System 2.8.0 of the fixture really is the 2.8.0 shape: the AutoIncrement type has only End, Format and
    ///     CurrentValue, and nothing System 2.9.0 added is left.
    /// </summary>
    [Fact]
    public async Task System280Fixture_HasTheShapeOfSystem280()
    {
        Assert.True(SystemCkIds.CkModelId.Version.CompareTo(new CkVersion(2, 9, 0)) >= 0,
            "the embedded System must be 2.9.0 or later");
        var system = await System280Async();
        Assert.Equal(System280, system.ModelId);
        var autoIncrement = Assert.Single(system.Types!, t => t.TypeId.ToString().StartsWith("AutoIncrement", StringComparison.Ordinal));
        Assert.Equal(["CurrentValue", "End", "Format"],
            autoIncrement.Attributes!.Select(a => a.AttributeName).Order(StringComparer.Ordinal));
        var json = await SerializeAsync(system);
        Assert.All(System290Markers, marker => Assert.DoesNotContain(marker, json, StringComparison.Ordinal));
    }

    // ---- setup -------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A throwaway tenant on System 2.8.0 with the energy-community models and seed, four billing documents and
    ///     three customers numbered by the counters, two customers with the explicit numbers of the simulation seed
    ///     (above End, kept as supplied), then upgraded to the embedded System by a tenant resolve. The body gets the
    ///     repository of the upgraded tenant.
    /// </summary>
    private async Task WithUpgradedTenantAsync(string prefix,
        Func<ITenantRepository, IMongoDatabase, ExistingData, Task> body)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        try
        {
            var tenant = await CreateChildTenantAsync(systemContext, tenantId);
            var database = GetTenantDatabase(tenant.DatabaseName);
            await PersistModelAsync(await System280Async(), tenant);
            Assert.Equal(System280.FullName, await InstalledAsync(database, "System"));

            foreach (var file in new[] { "ck-basic-2.yaml", "ck-basic.energy-2.yaml", "ck-energycommunity-5.yaml" })
            {
                await PersistModelAsync(await CompiledModelAsync(file), tenant);
            }

            var repository = tenant.GetTenantRepository();
            await ImportRtAsync(repository, await File.ReadAllTextAsync(Path.Combine(TestDataFolder,
                "energy-community-counters.yaml"), TestContext.Current.CancellationToken));

            var documents = Enumerable.Range(0, 4).Select(_ => BillingDocument()).ToList();
            var customers = Enumerable.Range(0, 3).Select(_ => Customer()).ToList();
            customers.Add(Customer("10001"));
            customers.Add(Customer("10002"));
            // Two batches, like separate billing runs; documents and customers of one batch share no counter.
            await ApplyAsync(repository, documents.Take(2).Concat(customers).Select(Insert).ToArray());
            await ApplyAsync(repository, documents.Skip(2).Select(Insert).ToArray());
            Assert.Equal(System280.FullName, await InstalledAsync(database, "System"));
            var before = await SnapshotAsync(database);

            var restoresBefore = Restores(tenantId);
            var upgraded = await ResolveAsync(systemContext, tenantId);
            Assert.Equal(SystemCkIds.CkModelId.FullName, await InstalledAsync(database, "System"));
            Assert.True(Restores(tenantId) > restoresBefore, "the resolve must have imported the embedded System");
            foreach (var model in new[] { "Basic-2.7.0", "Basic.Energy-2.1.0", "EnergyCommunity-5.1.0" })
            {
                Assert.Equal(1, await ModelStateAsync(database, model));
            }

            var existing = new ExistingData(documents, customers,
                documents.Select(d => Number(d, "DocumentNumber")).ToList(),
                customers.Select(c => Number(c, "CustomerNumber")).ToList(), before, await SnapshotAsync(database));
            await body(upgraded.GetTenantRepository(), database, existing);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     Writes a model into the tenant the way the import step does (<c>IDatabaseCkModelRepository.UpdateModelAsync</c>)
    ///     and unloads the tenant's CK cache. <c>ITenantContext.ImportCkModelAsync</c> is not used for the 2.8.0 phase:
    ///     its migration step resolves the tenant, and in this process, whose embedded System is 2.9.0, that resolve
    ///     upgrades the tenant at once - the tenant would never hold data on 2.8.0 (seen in run b318).
    /// </summary>
    private async Task PersistModelAsync(CkCompiledModelRoot model, ITenantContext tenant)
    {
        var databaseName = tenant.DatabaseName.ToLowerInvariant();
        var client = fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName);
        var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance, client,
            databaseName, tenant.TenantId);
        await fixture.GetService<IDatabaseCkModelRepository>()
            .UpdateModelAsync(model, new TenantDatabaseSourceIdentifier(null, dataSource, tenant.TenantId));
        var cache = fixture.GetService<ICkCacheService>();
        if (cache.IsTenantLoaded(tenant.TenantId))
        {
            cache.Unload(tenant.TenantId);
        }
    }

    /// <summary>
    ///     System 2.8.0: the embedded System with every element System 2.9.0 added removed (the six optional
    ///     AutoIncrement attributes, the AutoIncrementPeriod and NumberRangeRelease records, the AutoIncrementPeriodKinds
    ///     and DocumentAuditActions enums, the DocumentAuditEntry type with its attributes and the Cancels role) and its
    ///     id set to 2.8.0. 2.9.0 changed nothing else (AB#9752, System CK CHANGELOG).
    /// </summary>
    private async Task<CkCompiledModelRoot> System280Async()
    {
        var embedded = await fixture.GetService<ICatalogService>().GetAsync(SystemCkIds.CkModelId, new OperationResult());
        Assert.NotNull(embedded);
        var node = JsonNode.Parse(await SerializeAsync(embedded))!.AsObject();

        RemoveWhere(node["types"], "typeId", name => name == "DocumentAuditEntry");
        foreach (var type in (node["types"] as JsonArray ?? []).OfType<JsonObject>()
                     .Where(t => BaseName(t["typeId"]) == "AutoIncrement"))
        {
            RemoveWhere(type["attributes"], "id",
                name => System290AutoIncrementAttributes.Any(a => name == $"AutoIncrement.{a}"));
        }

        RemoveWhere(node["attributes"], "id", name =>
            System290AutoIncrementAttributes.Any(a => name == $"AutoIncrement.{a}") ||
            name.StartsWith("AutoIncrementPeriod.", StringComparison.Ordinal) ||
            name.StartsWith("NumberRangeRelease.", StringComparison.Ordinal) ||
            name.StartsWith("DocumentAuditEntry.", StringComparison.Ordinal));
        RemoveWhere(node["records"], "recordId", name => name is "AutoIncrementPeriod" or "NumberRangeRelease");
        RemoveWhere(node["enums"], "enumId", name => name is "AutoIncrementPeriodKinds" or "DocumentAuditActions");
        RemoveWhere(node["associationRoles"], "id", name => name == "Cancels");
        node["description"] = "System 2.8.0 shape (AB#9756 fixture)";

        var json = node.ToJsonString().Replace(SystemCkIds.CkModelId.FullName, System280.FullName);
        var operationResult = new OperationResult();
        var system = await fixture.GetService<ICkJsonSerializer>()
            .DeserializeCompiledModelRootAsync(json, "system-2.8.0", operationResult);
        Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));
        return system;
    }

    /// <summary>The base name of an element id: without the model prefix and the version suffix (<c>-1</c>).</summary>
    private static string BaseName(JsonNode? id)
    {
        var value = id?.GetValue<string>() ?? string.Empty;
        var slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        var dash = value.LastIndexOf('-');
        return dash > 0 && value[(dash + 1)..].All(char.IsDigit) ? value[..dash] : value;
    }

    private static void RemoveWhere(JsonNode? array, string idProperty, Func<string, bool> remove)
    {
        if (array is not JsonArray items)
        {
            return;
        }

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is JsonObject item && remove(BaseName(item[idProperty])))
            {
                items.RemoveAt(i);
            }
        }
    }

    private async Task<string> SerializeAsync(CkCompiledModelRoot model)
    {
        using var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await fixture.GetService<ICkJsonSerializer>().SerializeAsync(writer, model);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<CkCompiledModelRoot> CompiledModelAsync(string file)
    {
        var yaml = await File.ReadAllTextAsync(Path.Combine(TestDataFolder, file), TestContext.Current.CancellationToken);
        var operationResult = new OperationResult();
        var model = await fixture.GetService<ICkYamlSerializer>()
            .DeserializeCompiledModelRootAsync(yaml, file, operationResult);
        Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));
        return model;
    }

    // ---- data --------------------------------------------------------------------------------------------------------

    private static RtEntity BillingDocument(OctoObjectId? rtId = null, double grossTotal = 120.0) =>
        new(BillingDocumentType, rtId ?? OctoObjectId.GenerateNewId(), new Dictionary<string, object?>
        {
            ["DocumentDate"] = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            ["CustomerNumber"] = "1",
            ["Contact"] = Contact(),
            ["TimeRange"] = new RtRecord(new RtCkId<CkRecordId>("Basic/TimeRange"), new Dictionary<string, object?>
            {
                ["From"] = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                ["To"] = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)
            }),
            ["TaxProcedureCreditNote"] = 0,
            ["BillingType"] = 0,
            ["BillingDocumentState"] = 0,
            ["GrossTotal"] = grossTotal
        });

    private static RtEntity Customer(string? customerNumber = null)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["Contact"] = Contact(), ["BillingCycle"] = 1, ["State"] = 1, ["TaxProcedureCreditNote"] = 0
        };
        if (customerNumber != null)
        {
            attributes["CustomerNumber"] = customerNumber;
        }

        return new RtEntity(CustomerType, OctoObjectId.GenerateNewId(), attributes);
    }

    private static RtRecord Contact() => new(new RtCkId<CkRecordId>("Basic/Contact"),
        new Dictionary<string, object?> { ["LastName"] = "Muster" });

    /// <summary>Entities in the shape of an RT export: the required attributes and the number.</summary>
    private static string ExportYaml(params (RtCkId<CkTypeId> Type, OctoObjectId RtId, string NumberAttribute,
        string Number)[] entities)
    {
        var yaml = new StringBuilder($"$schema: {RtModelRootTcDto.RtSchemaUri}\n");
        yaml.Append("dependencies:\n  - System-[2.0,3.0)\n  - Basic-[2.4,3.0)\n  - Basic.Energy-[2.0,3.0)\n" +
                    "  - EnergyCommunity-[5.0,6.0)\nentities:\n");
        foreach (var (type, rtId, numberAttribute, number) in entities)
        {
            var isDocument = type.ToString() == BillingDocumentType.ToString();
            var model = isDocument ? "Basic" : "EnergyCommunity";
            yaml.Append($"  - rtId: {rtId}\n    ckTypeId: {type}\n    associations: []\n    attributes:\n");
            yaml.Append($"      - id: {model}/{numberAttribute}\n        value: '{number}'\n");
            yaml.Append("      - id: Basic/Contact\n        value:\n          ckRecordId: Basic/Contact\n" +
                        "          attributes:\n            - id: Basic/LastName\n              value: Muster\n");
            yaml.Append("      - id: EnergyCommunity/TaxProcedureCreditNote\n        value: 0\n");
            if (isDocument)
            {
                yaml.Append("      - id: Basic/DocumentDate\n        value: 2026-10-31T00:00:00Z\n" +
                            "      - id: EnergyCommunity/CustomerNumber\n        value: '1'\n" +
                            "      - id: Basic/TimeRange\n        value:\n          ckRecordId: Basic/TimeRange\n" +
                            "          attributes:\n            - id: Basic/From\n              value: 2026-10-01T00:00:00Z\n" +
                            "            - id: Basic/To\n              value: 2026-10-31T00:00:00Z\n" +
                            "      - id: EnergyCommunity/BillingType\n        value: 0\n" +
                            "      - id: EnergyCommunity/BillingDocumentState\n        value: 0\n" +
                            "      - id: EnergyCommunity/GrossTotal\n        value: 120.0\n");
            }
            else
            {
                yaml.Append("      - id: EnergyCommunity/BillingCycle\n        value: 1\n" +
                            "      - id: Basic.Energy/State\n        value: 1\n");
            }
        }

        return yaml.ToString();
    }

    private static IEntityUpdateInfo<RtEntity> Insert(RtEntity entity) => EntityUpdateInfo<RtEntity>.CreateInsert(entity);

    private static async Task ApplyAsync(ITenantRepository repository, params IEntityUpdateInfo<RtEntity>[] updates)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, updates, operationResult);
        Assert.False(operationResult.HasErrors, operationResult.GetMessages());
        await session.CommitTransactionAsync();
    }

    private async Task ImportRtAsync(ITenantRepository repository, string yaml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ab9756-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(file, yaml, TestContext.Current.CancellationToken);
        try
        {
            await fixture.GetService<IImportRtModelCommand>().ImportAsync(repository, file,
                ExchangeMimeTypes.MimeTypeYaml, ImportStrategy.Upsert);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Number(RtEntity entity, string attribute) =>
        Assert.IsType<string>(entity.GetAttributeValueOrDefault(attribute));

    // ---- database ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Every document of the tenant's own runtime collections (not the System ones) and of the counter collection,
    ///     as JSON, sorted by collection and id.
    /// </summary>
    private static async Task<List<string>> SnapshotAsync(IMongoDatabase database)
    {
        var snapshot = new List<string>();
        foreach (var collection in (await RtCollectionsAsync(database)).Order(StringComparer.Ordinal))
        {
            var documents = await database.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty)
                .SortBy(d => d["_id"]).ToListAsync(TestContext.Current.CancellationToken);
            snapshot.AddRange(documents.Select(d => $"{collection} {d.ToJson()}"));
        }

        Assert.Contains(snapshot, s => s.StartsWith(CounterCollection, StringComparison.Ordinal));
        Assert.Contains(snapshot, s => s.Contains("documentNumber", StringComparison.Ordinal));
        Assert.Contains(snapshot, s => s.Contains("customerNumber", StringComparison.Ordinal));
        return snapshot;
    }

    private static async Task<List<string>> RtCollectionsAsync(IMongoDatabase database)
    {
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        return names.Where(n => n == CounterCollection ||
                                (n.StartsWith(RtCollectionPrefix, StringComparison.Ordinal) &&
                                 !n.StartsWith(SystemRtCollectionPrefix, StringComparison.Ordinal))).ToList();
    }

    private static async Task<BsonDocument> FindRtAsync(IMongoDatabase database, OctoObjectId rtId)
    {
        foreach (var collection in await RtCollectionsAsync(database))
        {
            var document = await database.GetCollection<BsonDocument>(collection)
                .Find(new BsonDocument("_id", ObjectId.Parse(rtId.ToString())))
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
            if (document != null)
            {
                return document;
            }
        }

        throw new InvalidOperationException($"entity {rtId} is not stored");
    }

    private static async Task<BsonDocument> FindAsync(IMongoDatabase database, string collection, OctoObjectId rtId) =>
        await database.GetCollection<BsonDocument>(collection)
            .Find(new BsonDocument("_id", ObjectId.Parse(rtId.ToString())))
            .SingleAsync(TestContext.Current.CancellationToken);

    private static async Task<string> StoredNumberAsync(IMongoDatabase database, OctoObjectId rtId, string field) =>
        (await FindRtAsync(database, rtId))["attributes"][field].AsString;

    private static async Task<long> CounterValueAsync(IMongoDatabase database, OctoObjectId counterId) =>
        (await FindAsync(database, CounterCollection, counterId))["attributes"]["currentValue"].ToInt64();

    private static async Task<string> InstalledAsync(IMongoDatabase database, string modelName)
    {
        var documents = await database.GetCollection<BsonDocument>("CkModel")
            .Find(new BsonDocument("modelId", modelName)).ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(documents)["_id"].AsString;
    }

    private static async Task<int> ModelStateAsync(IMongoDatabase database, string modelId) =>
        (await database.GetCollection<BsonDocument>("CkModel").Find(new BsonDocument("_id", modelId))
            .SingleAsync(TestContext.Current.CancellationToken))["modelState"].AsInt32;

    private int Restores(string tenantId) =>
        fixture.Logs.Find(LogLevel.Information, "Restoring system CK Model", tenantId).Count;

    private static async Task<ITenantContext> CreateChildTenantAsync(ITenantContext parent, string tenantId)
    {
        using (var session = await parent.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await parent.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        return await ResolveAsync(parent, tenantId);
    }

    /// <summary>Resolves the child tenant, which imports the embedded System when the tenant holds an older one.</summary>
    private static async Task<ITenantContext> ResolveAsync(ITenantContext parent, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        var child = await parent.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return child;
    }

    private IMongoDatabase GetTenantDatabase(string databaseName)
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };
        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName.ToLowerInvariant());
    }

    /// <summary>The data written on System 2.8.0 and the snapshots before and after the upgrade.</summary>
    private sealed record ExistingData(
        List<RtEntity> Documents,
        List<RtEntity> Customers,
        List<string> DocumentNumbers,
        List<string> CustomerNumbers,
        List<string> Before,
        List<string> After);
}
