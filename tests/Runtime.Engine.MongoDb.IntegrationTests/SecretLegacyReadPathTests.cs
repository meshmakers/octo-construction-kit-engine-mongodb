using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Meshmakers.Octo.Runtime.Engine.Secrets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5533 (WP11 finding): a legacy string in a Secret slot - plaintext or <c>enc:v1</c>, at the top
///     level, in a record-array element and in a single record - is handed out as
///     <see cref="RtSecretValue.LegacyPlaintext" /> on EVERY repository read path, not only by the RT query
///     engine: by id (dynamic and typed), by ids (dynamic and typed), graph by id, the CK-cache-free
///     migration read and change-stream documents (full document and pre-image).
///     <para>
///         The write-back test pins the consequence: an entity read by id and saved again keeps the stored
///         legacy text unchanged. Before the fix the plain string reached the engine write step as new
///         input, which encrypts it as plaintext (the <c>enc:v1</c> token is lost) or - on this host without
///         a key ring - refuses the write.
///     </para>
///     The <c>enc:v1</c> vectors are produced with a key generated per test run, the plaintexts are random.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class SecretLegacyReadPathTests
{
    private static readonly RtCkId<CkTypeId> SecretHolderTypeId = new("Test/SecretHolder");
    private static readonly RtCkId<CkRecordId> CredentialRecordId = new("Test/CredentialEntry");

    private static readonly TimeSpan CursorWarmup = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan EventWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly byte[] _legacyKey;
    private readonly ISecretAttributeProtector _protector;

    public SecretLegacyReadPathTests(ImportTestCkModelFixture fixture) : this(fixture, RandomNumberGenerator.GetBytes(32))
    {
    }

    private SecretLegacyReadPathTests(ImportTestCkModelFixture fixture, byte[] legacyKey)
    {
        this.fixture = fixture;
        _legacyKey = legacyKey;
        _protector = CreateProtector(legacyKey);
    }

    private readonly ImportTestCkModelFixture fixture;

    [Fact]
    public async Task GetRtEntityByRtId_Dynamic_NormalisesLegacyStrings()
    {
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync(session, new RtEntityId(SecretHolderTypeId, rtId));

        Assert.NotNull(entity);
        AssertLegacyNormalised(entity!, legacy);
    }

    [Fact]
    public async Task GetRtEntityByRtId_Typed_NormalisesLegacyStrings()
    {
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        using var session = await repository.GetSessionAsync();
        var entity = await repository.GetRtEntityByRtIdAsync<RtSecretHolder>(session, rtId);

        Assert.NotNull(entity);
        AssertLegacyNormalised(entity!, legacy);

        // The generated RtSecretValue? property reads the normalised slot.
        Assert.NotNull(entity!.ApiKey);
        Assert.True(entity.ApiKey!.IsLegacyPlaintext);
        Assert.Equal(legacy.ApiKeyPlaintext, _protector.Unprotect(entity.ApiKey));
    }

    [Fact]
    public async Task GetRtEntitiesById_DynamicAndTyped_NormaliseLegacyStrings()
    {
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        using var session = await repository.GetSessionAsync();
        var dynamicResult = await repository.GetRtEntitiesByIdAsync(session, SecretHolderTypeId, [rtId],
            RtEntityQueryOptions.Create());
        AssertLegacyNormalised(Assert.Single(dynamicResult.Items), legacy);

        var typedResult = await repository.GetRtEntitiesByIdAsync<RtSecretHolder>(session, [rtId],
            RtEntityQueryOptions.Create());
        AssertLegacyNormalised(Assert.Single(typedResult.Items), legacy);
    }

    [Fact]
    public async Task GetRtEntitiesGraphById_NormalisesLegacyStrings()
    {
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        using var session = await repository.GetSessionAsync();
        var result = await repository.GetRtEntitiesGraphByIdAsync(session, SecretHolderTypeId, [rtId],
            RtEntityQueryOptions.Create(), []);

        AssertLegacyNormalised(Assert.Single(result.Items), legacy);
    }

    [Fact]
    public async Task GetRtEntitiesByTypeForMigration_NormalisesLegacyStrings()
    {
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        using var session = await repository.GetSessionAsync();
        var (entities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(session, SecretHolderTypeId);

        AssertLegacyNormalised(Assert.Single(entities, e => e.RtId == rtId), legacy);
    }

    [Fact]
    public async Task ReadByIdThenReplace_KeepsTheStoredLegacyTextUnchanged()
    {
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        RtEntity entity;
        using (var readSession = await repository.GetSessionAsync())
        {
            entity = (await repository.GetRtEntityByRtIdAsync(readSession, new RtEntityId(SecretHolderTypeId, rtId)))!;
        }

        entity.SetAttributeValue("Name", AttributeValueTypesDto.String, "renamed");

        using (var writeSession = await repository.GetSessionAsync())
        {
            writeSession.StartTransaction();
            await repository.ReplaceOneRtEntityByIdAsync(writeSession, SecretHolderTypeId, rtId, entity);
            await writeSession.CommitTransactionAsync();
        }

        var attributes = (await FindRawEntityDocumentAsync(rtId))!["attributes"].AsBsonDocument;
        Assert.Equal("renamed", attributes["name"].AsString);
        Assert.Equal(legacy.ApiKey, attributes["apiKey"].AsString);
        Assert.Equal(legacy.ArrayValue, attributes["credentials"][0]["attributes"]["value"].AsString);
        Assert.Equal(legacy.RecordValue, attributes["primaryCredential"]["attributes"]["value"].AsString);

        // The enc:v1 value is still the original token and still decrypts with the legacy key.
        Assert.Equal(legacy.ApiKeyPlaintext, DecryptV1(attributes["apiKey"].AsString));
    }

    [Fact]
    public async Task ChangeStream_NormalisesFullDocumentAndPreImage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (repository, rtId, legacy) = await SeedLegacyHolderAsync();

        using var stream = await repository.WatchRtEntitiesAsync(SecretHolderTypeId,
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Update, RtId = rtId }, ct);
        await Task.Delay(CursorWarmup, ct);

        await SetRawAttributesAsync(rtId, new BsonDocument("attributes.name", "changed"));

        var update = await stream.GetUpdates().FirstAsync().Timeout(EventWaitTimeout).ToTask(ct);
        Assert.NotNull(update.Document);
        AssertLegacyNormalised(update.Document!, legacy);
        // SecretHolder does not enable pre-images; when one is delivered it is normalised the same way.
        if (update.DocumentBeforeChange != null)
        {
            AssertLegacyNormalised(update.DocumentBeforeChange, legacy);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record LegacyValues(string ApiKey, string ApiKeyPlaintext, string ArrayValue, string RecordValue);

    /// <summary>
    ///     Inserts a holder and replaces its Secret slots in the raw document by legacy strings: an
    ///     <c>enc:v1</c> token at the top level, plaintext in the record-array element and in the single
    ///     record - the shapes a database holds before the encrypt sweep reached it.
    /// </summary>
    private async Task<(ITenantRepository Repository, OctoObjectId RtId, LegacyValues Legacy)> SeedLegacyHolderAsync()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();

        var entity = new RtEntity(SecretHolderTypeId, rtId, new Dictionary<string, object?>
        {
            ["Name"] = "legacy",
            ["Credentials"] = new List<RtRecord> { NewCredential("smtp") },
            ["PrimaryCredential"] = NewCredential("main")
        });
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, entity);
            await session.CommitTransactionAsync();
        }

        var apiKeyPlaintext = NewPlaintext();
        var legacy = new LegacyValues(EncryptV1(apiKeyPlaintext), apiKeyPlaintext, NewPlaintext(), NewPlaintext());
        await SetRawAttributesAsync(rtId, new BsonDocument
        {
            { "attributes.apiKey", legacy.ApiKey },
            { "attributes.credentials.0.attributes.value", legacy.ArrayValue },
            { "attributes.primaryCredential.attributes.value", legacy.RecordValue }
        });

        return (repository, rtId, legacy);
    }

    private void AssertLegacyNormalised(RtEntity entity, LegacyValues legacy)
    {
        var apiKey = Assert.IsType<RtSecretValue>(entity.Attributes["ApiKey"]);
        Assert.True(apiKey.IsLegacyPlaintext);
        // The enc:v1 token is decrypted with the legacy key - it was handed out unchanged.
        Assert.Equal(legacy.ApiKeyPlaintext, _protector.Unprotect(apiKey));
        Assert.Equal(RtSecretValue.Mask, apiKey.ToString());

        var element = Assert.Single(entity.GetRtRecordAttributeValues<RtRecord>("Credentials")!);
        var arrayValue = Assert.IsType<RtSecretValue>(element.Attributes["Value"]);
        Assert.True(arrayValue.IsLegacyPlaintext);
        Assert.Equal(legacy.ArrayValue, _protector.Unprotect(arrayValue));
        // Non-secret record members stay as they are.
        Assert.IsType<string>(element.Attributes["Key"]);

        var primary = entity.GetRtRecordAttributeValueOrDefault<RtRecord>("PrimaryCredential")!;
        var recordValue = Assert.IsType<RtSecretValue>(primary.Attributes["Value"]);
        Assert.True(recordValue.IsLegacyPlaintext);
        Assert.Equal(legacy.RecordValue, _protector.Unprotect(recordValue));

        Assert.IsType<string>(entity.Attributes["Name"]);
    }

    private static ISecretAttributeProtector CreateProtector(byte[] legacyKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.Configure<SecretEncryptionOptions>(options =>
        {
            options.Keys["t1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            options.ActiveKeyId = "t1";
            options.LegacyV1Key = Convert.ToBase64String(legacyKey);
        });
        return services.BuildServiceProvider().GetRequiredService<ISecretAttributeProtector>();
    }

    private static RtRecord NewCredential(string key) =>
        new(CredentialRecordId, new Dictionary<string, object?> { ["Key"] = key });

    private static string NewPlaintext() => "pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    ///     The legacy <c>enc:v1</c> format (octo-sdk <c>InstanceSecretCrypto</c>): AES-256-GCM without
    ///     associated data, <c>enc:v1:</c> + base64(nonce[12] ‖ tag[16] ‖ ciphertext).
    /// </summary>
    private string EncryptV1(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintextBytes.Length];
        using var aes = new AesGcm(_legacyKey, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        return SecretEnvelope.PrefixV1 + Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    private string DecryptV1(string envelope)
    {
        var combined = Convert.FromBase64String(envelope[SecretEnvelope.PrefixV1.Length..]);
        var plaintextBytes = new byte[combined.Length - 28];
        using var aes = new AesGcm(_legacyKey, 16);
        aes.Decrypt(combined.AsSpan(0, 12), combined.AsSpan(28), combined.AsSpan(12, 16), plaintextBytes);
        return Encoding.UTF8.GetString(plaintextBytes);
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
