using System.Collections;
using System.Text.Json;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

// ReSharper disable once ClassNeverInstantiated.Global
/// <summary>A system tenant of its own; every test creates (and drops) a throw-away child tenant.</summary>
public class ImportRtJsonAwaitFixture : SystemFixture;

[CollectionDefinition(Name)]
public class ImportRtJsonAwaitCollection : ICollectionFixture<ImportRtJsonAwaitFixture>
{
    public const string Name = "ImportRtJsonAwait";
}

/// <summary>
///     AB#8325 against a real MongoDB: a JSON <c>ImportRt</c> writes its bulks inside the awaited call. The entities are
///     readable as soon as the call returns, a write that fails (message 141 of AB#8141, a duplicate key) fails the call
///     and nothing of the file is committed. Before the fix the JSON bulks were written by an <c>async void</c> event
///     handler: the call returned and committed before the write (the entity appeared about 3 s later) and the write
///     errors never reached the caller.
/// </summary>
/// <remarks>
///     Model <c>JsonAwait</c> (ckLanguage 2): record <c>Item</c> (key <c>Key</c>; <c>Label</c>; <c>Token</c> MethodOnly), record
///     <c>Loose</c> (no key; <c>Label</c>; <c>Token</c> MethodOnly), record <c>LooseBase</c> (no key; <c>Label</c>) and the
///     derived <c>LooseSpecial</c> (adds <c>Token</c> MethodOnly). Type <c>Holder</c>: <c>Name</c>, <c>Items</c> (Item[]),
///     <c>Looses</c> (Loose[]), <c>LooseEntries</c> (LooseBase[]). Stored protected values are written through the repository
///     (a repository write is not an import; the import refuses an explicit value with 139).
/// </remarks>
[Collection(ImportRtJsonAwaitCollection.Name)]
public class ImportRtJsonAwaitIntegrationTests(ImportRtJsonAwaitFixture fixture)
{
    private const string Model = "JsonAwait";
    private const string JsonMimeType = "application/json";
    private static readonly RtCkId<CkTypeId> HolderType = new($"{Model}/Holder");
    private static readonly RtImportCallerContext Caller = RtImportCallerContext.UserInitiated("user-ab8325");

    [Fact]
    public async Task JsonInsert_NewEntity_IsReadableWhenTheCallReturns()
    {
        await WithTenantAsync("jsonins", async tenant =>
        {
            var rtId = OctoObjectId.GenerateNewId();

            await ImportAsync(tenant, Holder(rtId, "fresh"), ImportStrategy.Insert);

            // No waiting: the call returns after the write was committed.
            Assert.Equal("fresh", (await ReadAsync(tenant, rtId)).Attributes["Name"]);
        });
    }

    [Fact]
    public async Task JsonCallerUpsert_OmittedMethodOnlyMember_IsKept_WhenTheCallReturns()
    {
        await WithTenantAsync("jsonkeep", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("Items", new List<RtRecord> { NewRecord("Item", ("Key", "a"),
                ("Label", "stored-a"), ("Token", "tok-a")) }));

            await ImportAsCallerAsync(tenant, Holder(rtId, "n", ("Items", [Rec("Item", ("Key", "a"), ("Label", "A2"))])));

            var item = Records((await ReadAsync(tenant, rtId)).Attributes["Items"]).Single();
            Assert.Equal("A2", item.Attributes["Label"]);
            Assert.Equal("tok-a", item.Attributes["Token"]);
        });
    }

    [Fact]
    public async Task JsonCallerUpsert_KeylessRecordArrayWithAStoredProtectedValue_IsRefusedWith141_AndNothingIsWritten()
    {
        await WithTenantAsync("json141", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("Looses", new List<RtRecord> { NewRecord("Loose", ("Label", "l"),
                ("Token", "secret")) }));

            var ex = await Assert.ThrowsAsync<ExchangeException>(() => ImportAsCallerAsync(tenant,
                Holder(rtId, "n", ("Looses", [Rec("Loose", ("Label", "l2"))]))));

            Assert.Equal(141, ex.MessageNumber);
            Assert.Contains("Looses[0].Token", ex.Message);
            await AssertLooseUnchangedAsync(tenant, rtId, "Looses");
        });
    }

    [Fact]
    public async Task JsonCallerUpsert_KeylessArrayOfADerivedRecordWithAStoredProtectedValue_IsRefusedWith141_AndNothingIsWritten()
    {
        await WithTenantAsync("json141d", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("LooseEntries", new List<RtRecord> { NewRecord("LooseSpecial",
                ("Label", "l"), ("Token", "secret")) }));

            var ex = await Assert.ThrowsAsync<ExchangeException>(() => ImportAsCallerAsync(tenant,
                Holder(rtId, "n", ("LooseEntries", [Rec("LooseSpecial", ("Label", "l2"))]))));

            Assert.Equal(141, ex.MessageNumber);
            Assert.Contains("LooseEntries[0].Token", ex.Message);
            await AssertLooseUnchangedAsync(tenant, rtId, "LooseEntries");
        });
    }

    [Fact]
    public async Task JsonInsert_FailingWriteInTheFile_FailsTheCall_AndCommitsNoEntityOfTheFile()
    {
        await WithTenantAsync("jsondup", async tenant =>
        {
            var existing = await StoreAsync(tenant);
            var fresh = OctoObjectId.GenerateNewId();

            // Insert of an active stored rtId fails on the duplicate key; the new entity of the same file must not stay.
            await Assert.ThrowsAnyAsync<Exception>(() => ImportAsync(tenant,
                Holders((fresh, "fresh"), (existing, "dup")), ImportStrategy.Insert));

            Assert.Empty(await TryReadAsync(tenant, fresh));
            Assert.Equal("stored", (await ReadAsync(tenant, existing)).Attributes["Name"]);
        });
    }

    private async Task AssertLooseUnchangedAsync(ITenantContext tenant, OctoObjectId rtId, string attribute)
    {
        var stored = await ReadAsync(tenant, rtId);
        Assert.Equal("stored", stored.Attributes["Name"]);
        var loose = Records(stored.Attributes[attribute]).Single();
        Assert.Equal("l", loose.Attributes["Label"]);
        Assert.Equal("secret", loose.Attributes["Token"]);
    }

    // ---- model ------------------------------------------------------------------------------------------------------

    private static CkCompiledModelRoot JsonAwaitModel(CkModelId systemId)
    {
        var id = new CkModelId($"{Model}-1.0.0");

        CkTypeAttributeDto Assign(string name, CkAttributeAccessDto? access = null) => new()
        {
            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId($"{name}-1")), AttributeName = name,
            IsOptional = true, Access = access
        };

        CkAttributeDto Define(string name, AttributeValueTypesDto type = AttributeValueTypesDto.String,
            string? record = null) => new()
        {
            AttributeId = new CkAttributeId($"{name}-1"), ValueType = type,
            ValueCkRecordId = record == null ? null : new CkId<CkRecordId>(id, new CkRecordId($"{record}-1"))
        };

        CkRecordDto Record(string name, string? key, string? derivedFrom, params CkTypeAttributeDto[] attributes) => new()
        {
            RecordId = new CkRecordId($"{name}-1"), RecordKey = key, Derivable = CkDerivableDto.Any,
            DerivedFromCkRecordId = derivedFrom == null ? null : new CkId<CkRecordId>(id, new CkRecordId($"{derivedFrom}-1")),
            Attributes = attributes.ToList()
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            CkLanguage = 2,
            MinEngineVersion = "3.4.0",
            Dependencies = [systemId],
            Attributes =
            [
                Define("Name"), Define("Key"), Define("Label"), Define("Token"),
                Define("Items", AttributeValueTypesDto.RecordArray, "Item"),
                Define("Looses", AttributeValueTypesDto.RecordArray, "Loose"),
                Define("LooseEntries", AttributeValueTypesDto.RecordArray, "LooseBase")
            ],
            Records =
            [
                Record("Item", "Key", null, Assign("Key"), Assign("Label"), Assign("Token", CkAttributeAccessDto.MethodOnly)),
                Record("Loose", null, null, Assign("Label"), Assign("Token", CkAttributeAccessDto.MethodOnly)),
                Record("LooseBase", null, null, Assign("Label")),
                Record("LooseSpecial", null, "LooseBase", Assign("Token", CkAttributeAccessDto.MethodOnly))
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Holder-1"), IsCollectionRoot = true, Derivable = CkDerivableDto.Any,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                    Attributes = [Assign("Name"), Assign("Items"), Assign("Looses"), Assign("LooseEntries")]
                }
            ]
        };
    }

    // ---- stored values (repository write) ------------------------------------------------------------------------------

    private static RtRecord NewRecord(string record, params (string Name, object? Value)[] attributes)
    {
        var rtRecord = new RtRecord { CkRecordId = new RtCkId<CkRecordId>($"{Model}/{record}") };
        foreach (var (name, value) in attributes.Where(a => a.Value != null))
        {
            rtRecord.SetAttributeRawValue(name, value);
        }

        return rtRecord;
    }

    private static async Task<OctoObjectId> StoreAsync(ITenantContext tenant, params (string Name, object Value)[] attributes)
    {
        var repository = tenant.GetTenantRepository();
        var entity = await repository.CreateTransientRtEntityByRtCkIdAsync(HolderType);
        entity.SetAttributeRawValue("Name", "stored");
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, HolderType, entity);
        await session.CommitTransactionAsync();
        return entity.RtId;
    }

    private static async Task<IReadOnlyList<RtEntity>> TryReadAsync(ITenantContext tenant, OctoObjectId rtId)
    {
        var repository = tenant.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByIdAsync(session, HolderType, [rtId], RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result.Items.ToList();
    }

    private static async Task<RtEntity> ReadAsync(ITenantContext tenant, OctoObjectId rtId) =>
        Assert.Single(await TryReadAsync(tenant, rtId));

    private static List<RtRecord> Records(object? value) =>
        Assert.IsAssignableFrom<IEnumerable>(value).Cast<object>().Select(Assert.IsType<RtRecord>).ToList();

    // ---- import file (JSON, as ImportRt reads it) ------------------------------------------------------------------

    private static Dictionary<string, object?> Rec(string record, params (string Name, string Value)[] members) => new()
    {
        ["ckRecordId"] = $"{Model}/{record}",
        ["attributes"] = members.Select(m => new Dictionary<string, object?> { ["id"] = $"{Model}/{m.Name}", ["value"] = m.Value })
            .ToList()
    };

    private static Dictionary<string, object?> Entity(OctoObjectId rtId, string name,
        params (string Name, object Value)[] attributes)
    {
        var values = new List<Dictionary<string, object?>> { new() { ["id"] = $"{Model}/Name", ["value"] = name } };
        values.AddRange(attributes.Select(a => new Dictionary<string, object?> { ["id"] = $"{Model}/{a.Name}", ["value"] = a.Value }));
        return new Dictionary<string, object?>
        {
            ["rtId"] = rtId.ToString(), ["ckTypeId"] = HolderType.ToString(), ["attributes"] = values,
            ["associations"] = new List<object>()
        };
    }

    private static string File(params Dictionary<string, object?>[] entities) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["$schema"] = RtModelRootTcDto.RtSchemaUri, ["dependencies"] = new[] { $"{Model}-[1.0,2.0)" },
            ["entities"] = entities
        });

    /// <summary>One Holder with <c>Name</c> and the given record-array attributes.</summary>
    private static string Holder(OctoObjectId rtId, string name, params (string Name, Dictionary<string, object?>[] Records)[] arrays) =>
        File(Entity(rtId, name, arrays.Select(a => (a.Name, (object)a.Records)).ToArray()));

    private static string Holders(params (OctoObjectId RtId, string Name)[] holders) =>
        File(holders.Select(h => Entity(h.RtId, h.Name)).ToArray());

    private Task ImportAsCallerAsync(ITenantContext tenant, string json) =>
        WithFileAsync(json, file => fixture.GetService<IImportRtModelCommand>().ImportAsCallerAsync(
            tenant.GetTenantRepository(), file, JsonMimeType, ImportStrategy.Upsert, Caller,
            blankingPolicy: RtImportBlankingPolicy.Allow));

    private Task ImportAsync(ITenantContext tenant, string json, ImportStrategy strategy) =>
        WithFileAsync(json, file => fixture.GetService<IImportRtModelCommand>().ImportAsync(
            tenant.GetTenantRepository(), file, JsonMimeType, strategy));

    private static async Task WithFileAsync(string json, Func<string, Task> import)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ab8325-{Guid.NewGuid():N}.json");
        await System.IO.File.WriteAllTextAsync(file, json, TestContext.Current.CancellationToken);
        try
        {
            await import(file);
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    // ---- tenant ------------------------------------------------------------------------------------------------------

    private async Task WithTenantAsync(string prefix, Func<ITenantContext, Task> body)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            ITenantContext tenant;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                tenant = await systemContext.GetChildTenantContextAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            await tenant.ImportCkModelAsync(JsonAwaitModel(await InstalledSystemAsync(tenant)));
            await tenant.LoadCacheForTenantAsync();
            await body(tenant);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId, dropStreamData: false);
        }
    }

    private async Task<CkModelId> InstalledSystemAsync(ITenantContext tenant)
    {
        var catalogService = fixture.GetService<ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await tenant.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found in the catalogs.");
    }
}
