using System.Security.Cryptography;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5533 (live defect 2026-10-06): the Encrypt sweep against the real MongoDB repository. Legacy clear text in
///     Secret slots - at the top level, in a record and in a record array - of a collection-root type AND of a
///     derived type (stored in the root's collection, like every <c>System.Communication/*Configuration</c> in
///     <c>RtEntity_SystemConfiguration</c>) must end up as <c>{ _t: "OctoSecret", e: "enc:v2:k1:…" }</c> (no <c>t</c>: set-at unknown for converted values). Before
///     the fix the conditional rewrite looked for derived-type entities in a collection named after their own type,
///     matched nothing and the sweep reported every value as "modified concurrently" while reporting success.
///     Keys and "plaintexts" are generated per run; no value is ever printed.
/// </summary>
[Collection(SecretSweepCollection.Name)]
public class SecretSweepStorageTests(SecretSweepFixture fixture)
{
    private static readonly RtCkId<CkTypeId> RootTypeId = new("Test/SecretHolder");
    private static readonly RtCkId<CkTypeId> DerivedTypeId = new("Test/SecretHolderVariant");
    private static readonly RtCkId<CkRecordId> CredentialRecordId = new("Test/CredentialEntry");

    [Fact]
    public async Task Encrypt_ConvertsLegacyPlaintext_TopLevelAndInRecords_OfRootAndDerivedTypes()
    {
        await fixture.ClearCollectionAsync();
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var rootId = OctoObjectId.GenerateNewId();
        var derivedId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(RootTypeId, rootId));
        await InsertAsync(repository, NewHolder(DerivedTypeId, derivedId));

        // Legacy data as it exists before the SECRET value type: plain strings in every Secret slot.
        var plaintexts = new Dictionary<OctoObjectId, string[]>
        {
            [rootId] = [NewPlaintext(), NewPlaintext(), NewPlaintext()],
            [derivedId] = [NewPlaintext(), NewPlaintext(), NewPlaintext()]
        };
        foreach (var (rtId, values) in plaintexts)
        {
            await SetRawAttributesAsync(rtId, new BsonDocument
            {
                { "attributes.apiKey", values[0] },
                { "attributes.credentials.0.attributes.value", values[1] },
                { "attributes.primaryCredential.attributes.value", values[2] }
            });
        }

        // The derived entity lives in its collection root's collection; no collection of its own exists.
        var rootCollection = await FindRawEntityCollectionAsync(rootId);
        var derivedCollection = await FindRawEntityCollectionAsync(derivedId);
        Assert.Equal(rootCollection!.CollectionNamespace.CollectionName,
            derivedCollection!.CollectionNamespace.CollectionName);

        var sweep = fixture.GetService<ISecretMaintenanceService>();
        var result = await sweep.SweepTenantAsync(systemContext.TenantId, SecretSweepMode.Encrypt,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, result.SkippedConcurrentlyModified);
        Assert.Equal(6, TestSlots(result).Sum(s => s.Counts.Plaintext));
        Assert.Equal(6, result.ValuesEncrypted);
        Assert.Equal(result.ValuesEncrypted, result.ValuesRewritten);

        var protector = fixture.GetService<ISecretAttributeProtector>();
        foreach (var (rtId, values) in plaintexts)
        {
            var raw = (await FindRawEntityDocumentAsync(rtId))!;
            var json = raw.ToJson();
            var attributes = raw["attributes"].AsBsonDocument;
            var stored = new[]
            {
                attributes["apiKey"],
                attributes["credentials"][0]["attributes"]["value"],
                attributes["primaryCredential"]["attributes"]["value"]
            };
            for (var i = 0; i < values.Length; i++)
            {
                AssertStoredSecretShape(stored[i]);
                Assert.DoesNotContain(values[i], json, StringComparison.Ordinal);
                Assert.Equal(values[i],
                    protector.Unprotect(RtSecretValue.Protected(stored[i]["e"].AsString)));
            }
        }

        // The state after the run, and a second Encrypt has nothing left to do.
        var verify = await sweep.SweepTenantAsync(systemContext.TenantId, SecretSweepMode.Verify,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, TestSlots(verify).Sum(s => s.Counts.Plaintext));
        Assert.Equal(6, TestSlots(verify).Sum(s => s.Counts.EncV2));

        var second = await sweep.SweepTenantAsync(systemContext.TenantId, SecretSweepMode.Encrypt,
            TestContext.Current.CancellationToken);
        Assert.True(second.Success);
        Assert.Equal(0, second.ValuesRewritten);
        Assert.Equal(0, second.ValuesEncrypted);
        Assert.Equal(0, second.SkippedConcurrentlyModified);
    }

    [Fact]
    public async Task ConditionalRewrite_OfADerivedTypeEntity_FindsItInTheCollectionRoot()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(DerivedTypeId, rtId));
        var legacy = NewPlaintext();
        await SetRawAttributesAsync(rtId, new BsonDocument("attributes.apiKey", legacy));
        var protector = fixture.GetService<ISecretAttributeProtector>();
        var newValue = protector.Protect(NewPlaintext());

        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            Assert.True(await repository.RewriteAttributeValueIfUnchangedForMigrationAsync(session, DerivedTypeId,
                rtId, "ApiKey", RtSecretValue.LegacyPlaintext(legacy), newValue));
            await session.CommitTransactionAsync();
        }

        Assert.Equal(newValue.Envelope,
            (await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"]["e"].AsString);

        // The unconditional migration rewrite addresses the same document.
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.RewriteAttributeValueForMigrationAsync(session, DerivedTypeId, rtId, "ApiKey", null);
            await session.CommitTransactionAsync();
        }

        Assert.Equal(BsonNull.Value, (await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"]);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<SecretSlotReport> TestSlots(SecretSweepResult result) =>
        result.Slots.Where(s => s.CkTypeId.Contains("SecretHolder", StringComparison.Ordinal));

    private static string NewPlaintext() => "pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static RtRecord NewCredential(string key) =>
        new(CredentialRecordId, new Dictionary<string, object?> { ["Key"] = key, ["Value"] = null });

    private static RtEntity NewHolder(RtCkId<CkTypeId> typeId, OctoObjectId rtId) =>
        new(typeId, rtId, new Dictionary<string, object?>
        {
            ["Name"] = "sweep",
            ["Credentials"] = new List<RtRecord> { NewCredential("smtp") },
            ["PrimaryCredential"] = NewCredential("main")
        });

    private static void AssertStoredSecretShape(BsonValue value)
    {
        var document = Assert.IsType<BsonDocument>(value);
        // { _t, e }: a value converted from legacy storage has no set-at timestamp (concept Q2, handover §13 -
        // when it was set is unknown); 't' is stamped only on new input.
        Assert.Equal(2, document.ElementCount);
        Assert.Equal("OctoSecret", document["_t"].AsString);
        Assert.False(document.Contains("t"));
        var envelope = document["e"].AsString;
        Assert.StartsWith(SecretEnvelope.PrefixV2 + SecretSweepFixture.KeyId + ":", envelope, StringComparison.Ordinal);
    }

    private static async Task InsertAsync(ITenantRepository repository, RtEntity entity)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            await repository.InsertOneRtEntityAsync(session, entity);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    private async Task SetRawAttributesAsync(OctoObjectId rtId, BsonDocument set)
    {
        var collection = await FindRawEntityCollectionAsync(rtId);
        Assert.NotNull(collection);
        var result = await collection!.UpdateOneAsync(new BsonDocument("_id", new ObjectId(rtId.ToString())),
            new BsonDocument("$set", set));
        Assert.Equal(1, result.MatchedCount);
    }

    private async Task<BsonDocument?> FindRawEntityDocumentAsync(OctoObjectId rtId)
    {
        var collection = await FindRawEntityCollectionAsync(rtId);
        return collection == null
            ? null
            : await collection.Find(new BsonDocument("_id", new ObjectId(rtId.ToString()))).FirstOrDefaultAsync();
    }

    private async Task<IMongoCollection<BsonDocument>?> FindRawEntityCollectionAsync(OctoObjectId rtId)
    {
        var db = GetSystemMongoDatabase();
        var filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity_"));
        using var cursor = await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        var id = new ObjectId(rtId.ToString());
        foreach (var info in await cursor.ToListAsync())
        {
            var collection = db.GetCollection<BsonDocument>(info["name"].AsString);
            if (await collection.CountDocumentsAsync(new BsonDocument("_id", id)) > 0)
            {
                return collection;
            }
        }

        return null;
    }

    private IMongoDatabase GetSystemMongoDatabase()
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

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(config.SystemDatabaseName);
    }
}
