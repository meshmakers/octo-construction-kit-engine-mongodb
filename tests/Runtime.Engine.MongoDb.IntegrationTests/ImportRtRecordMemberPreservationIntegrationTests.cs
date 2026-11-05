using System.Collections;

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
public class ImportRtRecordMemberPreservationFixture : SystemFixture;

[CollectionDefinition(Name)]
public class ImportRtRecordMemberPreservationCollection : ICollectionFixture<ImportRtRecordMemberPreservationFixture>
{
    public const string Name = "ImportRtRecordMemberPreservation";
}

/// <summary>
///     AB#8141 against a real MongoDB and a CK model resolved by the engine: a user-initiated <c>ImportRt</c> upsert
///     (<c>ImportAsCallerAsync</c>) that writes a record without a member only a CK method may write (<c>access: MethodOnly</c>)
///     keeps the stored value - single record attribute by position, record array element by record key, and a member that
///     exists only on a record derived from the declared one. A record array without a record key cannot be matched: the
///     import is refused with message 141 and nothing is written. The stored values are written through the repository (a
///     repository write is not an import; the import refuses an explicit value with 139) and read back from MongoDB.
/// </summary>
/// <remarks>
///     Model <c>RecMem</c> (ckLanguage 2): record <c>Item</c> (key <c>Key</c>; <c>Label</c>; <c>Token</c> MethodOnly), record
///     <c>Loose</c> (no key; <c>Label</c>; <c>Token</c> MethodOnly), record <c>Entry</c> (key <c>Key</c>; <c>Label</c>; no
///     protected member) and the derived <c>SpecialEntry</c> (adds <c>Token</c> MethodOnly), record <c>LooseBase</c> (no key;
///     <c>Label</c>) and the derived <c>LooseSpecial</c> (adds <c>Token</c> MethodOnly). Type <c>Holder</c>: <c>Name</c>,
///     <c>Single</c> (Item), <c>Items</c> (Item[]), <c>Looses</c> (Loose[]), <c>Entries</c> (Entry[]), <c>LooseEntries</c>
///     (LooseBase[]). The import file is YAML: the JSON path of ImportRt writes its bulks from an <c>async void</c> handler and
///     returns before they are written (AB#8325), and the YAML reader reads only scalar member values inside a record, so
///     nested records are covered by the engine's unit tests only.
/// </remarks>
[Collection(ImportRtRecordMemberPreservationCollection.Name)]
public class ImportRtRecordMemberPreservationIntegrationTests(ImportRtRecordMemberPreservationFixture fixture)
{
    private const string Model = "RecMem";
    private static readonly RtCkId<CkTypeId> HolderType = new($"{Model}/Holder");
    private static readonly RtImportCallerContext Caller = RtImportCallerContext.UserInitiated("user-ab8141");

    [Fact]
    public async Task SingleRecord_OmittedMethodOnlyMember_IsKept()
    {
        await WithTenantAsync("recsingle", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("Single", Item("k", "stored", "tok")));

            await ImportAsCallerAsync(tenant, Holder(rtId, ("Single", Rec("Item", ("Key", "k"), ("Label", "new")))));

            var single = Assert.IsType<RtRecord>((await ReadAsync(tenant, rtId)).Attributes["Single"]);
            Assert.Equal("new", single.Attributes["Label"]);
            Assert.Equal("tok", single.Attributes["Token"]);
        });
    }

    [Fact]
    public async Task RecordArray_ElementsAreMatchedByRecordKey()
    {
        await WithTenantAsync("recarray", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("Items", new List<RtRecord>
            {
                Item("a", "stored-a", "tok-a"), Item("b", "stored-b", "tok-b")
            }));

            // Reordered, one new element without a stored counterpart.
            await ImportAsCallerAsync(tenant, Holder(rtId, ("Items", Array(
                Rec("Item", ("Key", "b"), ("Label", "B2")),
                Rec("Item", ("Key", "a"), ("Label", "A2")),
                Rec("Item", ("Key", "c"), ("Label", "C"))))));

            var items = Records((await ReadAsync(tenant, rtId)).Attributes["Items"]);
            Assert.Equal(["b", "a", "c"], items.Select(i => i.Attributes["Key"]));
            Assert.Equal("tok-b", items[0].Attributes["Token"]);
            Assert.Equal("A2", items[1].Attributes["Label"]);
            Assert.Equal("tok-a", items[1].Attributes["Token"]);
            Assert.False(items[2].Attributes.TryGetValue("Token", out var none) && none != null);
        });
    }

    [Fact]
    public async Task KeylessRecordArray_WithAStoredProtectedValue_IsRefusedWith141_AndNothingIsWritten()
    {
        await WithTenantAsync("reckeyless", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("Looses", new List<RtRecord> { Loose("Loose", "l", "secret") }));

            var ex = await Assert.ThrowsAsync<ExchangeException>(() => ImportAsCallerAsync(tenant,
                Holder(rtId, ("Looses", Array(Rec("Loose", ("Label", "l2")))))));

            Assert.Equal(141, ex.MessageNumber);
            Assert.Contains("Looses[0].Token", ex.Message);
            var stored = await ReadAsync(tenant, rtId);
            Assert.Equal("stored", stored.Attributes["Name"]);
            var loose = Records(stored.Attributes["Looses"]).Single();
            Assert.Equal("l", loose.Attributes["Label"]);
            Assert.Equal("secret", loose.Attributes["Token"]);
        });
    }

    [Fact]
    public async Task DerivedRecordInAnArray_OmittedMethodOnlyMemberOfTheDerivedRecord_IsKept()
    {
        await WithTenantAsync("recderived", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("Entries", new List<RtRecord>
            {
                Entry("SpecialEntry", "a", "tok-a"), Entry("Entry", "b", null)
            }));

            await ImportAsCallerAsync(tenant, Holder(rtId, ("Entries", Array(
                Rec("SpecialEntry", ("Key", "a"), ("Label", "A2")), Rec("Entry", ("Key", "b"), ("Label", "B2"))))));

            var entries = Records((await ReadAsync(tenant, rtId)).Attributes["Entries"]);
            Assert.StartsWith($"{Model}/SpecialEntry", entries[0].CkRecordId!.FullName);
            Assert.Equal("A2", entries[0].Attributes["Label"]);
            Assert.Equal("tok-a", entries[0].Attributes["Token"]);
            Assert.Equal("B2", entries[1].Attributes["Label"]);
        });
    }

    [Fact]
    public async Task KeylessArrayOfADerivedRecord_WithAStoredProtectedValue_IsRefusedWith141()
    {
        await WithTenantAsync("recderivedkl", async tenant =>
        {
            var rtId = await StoreAsync(tenant, ("LooseEntries", new List<RtRecord> { Loose("LooseSpecial", "l", "secret") }));

            var ex = await Assert.ThrowsAsync<ExchangeException>(() => ImportAsCallerAsync(tenant,
                Holder(rtId, ("LooseEntries", Array(Rec("LooseSpecial", ("Label", "l2")))))));

            Assert.Equal(141, ex.MessageNumber);
            Assert.Contains("LooseEntries[0].Token", ex.Message);
            var stored = await ReadAsync(tenant, rtId);
            Assert.Equal("stored", stored.Attributes["Name"]);
            var loose = Records(stored.Attributes["LooseEntries"]).Single();
            Assert.Equal("l", loose.Attributes["Label"]);
            Assert.Equal("secret", loose.Attributes["Token"]);
        });
    }

    // ---- model ------------------------------------------------------------------------------------------------------

    private static CkCompiledModelRoot RecMemModel(CkModelId systemId)
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
                Define("Single", AttributeValueTypesDto.Record, "Item"),
                Define("Items", AttributeValueTypesDto.RecordArray, "Item"),
                Define("Looses", AttributeValueTypesDto.RecordArray, "Loose"),
                Define("Entries", AttributeValueTypesDto.RecordArray, "Entry"),
                Define("LooseEntries", AttributeValueTypesDto.RecordArray, "LooseBase")
            ],
            Records =
            [
                Record("Item", "Key", null, Assign("Key"), Assign("Label"), Assign("Token", CkAttributeAccessDto.MethodOnly)),
                Record("Loose", null, null, Assign("Label"), Assign("Token", CkAttributeAccessDto.MethodOnly)),
                Record("Entry", "Key", null, Assign("Key"), Assign("Label")),
                Record("SpecialEntry", null, "Entry", Assign("Token", CkAttributeAccessDto.MethodOnly)),
                Record("LooseBase", null, null, Assign("Label")),
                Record("LooseSpecial", null, "LooseBase", Assign("Token", CkAttributeAccessDto.MethodOnly))
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Holder-1"), IsCollectionRoot = true, Derivable = CkDerivableDto.Any,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                    Attributes =
                    [
                        Assign("Name"), Assign("Single"), Assign("Items"), Assign("Looses"), Assign("Entries"),
                        Assign("LooseEntries")
                    ]
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

    private static RtRecord Item(string key, string label, string token) =>
        NewRecord("Item", ("Key", key), ("Label", label), ("Token", token));

    private static RtRecord Loose(string record, string label, string token) =>
        NewRecord(record, ("Label", label), ("Token", token));

    private static RtRecord Entry(string record, string key, string? token) =>
        NewRecord(record, ("Key", key), ("Label", "stored"), ("Token", token));

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

    private static async Task<RtEntity> ReadAsync(ITenantContext tenant, OctoObjectId rtId)
    {
        var repository = tenant.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByIdAsync(session, HolderType, [rtId], RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return Assert.Single(result.Items);
    }

    private static List<RtRecord> Records(object? value) =>
        Assert.IsAssignableFrom<IEnumerable>(value).Cast<object>().Select(Assert.IsType<RtRecord>).ToList();

    // ---- import file (YAML, as ImportRt reads it) ------------------------------------------------------------------

    /// <summary>A record value with scalar members (the YAML reader of ImportRt reads only scalar member values in a record).</summary>
    private static string Rec(string record, params (string Name, string Value)[] members) =>
        $"ckRecordId: {Model}/{record}\nattributes:\n" +
        string.Concat(members.Select(m => $"  - id: {Model}/{m.Name}\n    value: {m.Value}\n"));

    /// <summary>A record array: one sequence entry per record.</summary>
    private static string Array(params string[] records) =>
        string.Concat(records.Select(r => "- " + Indent(r, 2)[2..]));

    private static string Indent(string text, int columns) =>
        string.Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => new string(' ', columns) + l + "\n"));

    /// <summary>The Holder <paramref name="rtId" /> with <c>Name</c> and the given record attributes (YAML values).</summary>
    private static string Holder(OctoObjectId rtId, params (string Name, string Value)[] attributes)
    {
        var yaml = $"$schema: {RtModelRootTcDto.RtSchemaUri}\n" +
                   $"dependencies:\n  - {Model}-[1.0,2.0)\n" +
                   $"entities:\n  - rtId: {rtId}\n    ckTypeId: {HolderType}\n    associations: []\n    attributes:\n" +
                   $"      - id: {Model}/Name\n        value: n\n";
        foreach (var (name, value) in attributes)
        {
            yaml += $"      - id: {Model}/{name}\n        value:\n" + Indent(value, 10);
        }

        return yaml;
    }

    private async Task ImportAsCallerAsync(ITenantContext tenant, string yaml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ab8141-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(file, yaml, TestContext.Current.CancellationToken);
        try
        {
            await fixture.GetService<IImportRtModelCommand>().ImportAsCallerAsync(tenant.GetTenantRepository(), file,
                ExchangeMimeTypes.MimeTypeYaml, ImportStrategy.Upsert, Caller, blankingPolicy: RtImportBlankingPolicy.Allow);
        }
        finally
        {
            File.Delete(file);
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

            await tenant.ImportCkModelAsync(RecMemModel(await InstalledSystemAsync(tenant)));
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
