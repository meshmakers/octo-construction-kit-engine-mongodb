using System.Security.Cryptography;
using System.Text;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
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
///     AB#5533: storage of the SECRET attribute value type in MongoDB (concept §3.3, §3.4, §4.4).
///     <list type="bullet">
///         <item>a protected value is stored as <c>{ _t: "OctoSecret", e: "enc:v2:..." }</c> - at the top
///         level and inside record / record-array elements - and the plaintext bytes never reach the
///         raw document;</item>
///         <item>pending values are refused by the serializer on every write path;</item>
///         <item>a legacy string in a Secret slot reads back as <see cref="RtSecretValue.LegacyPlaintext" />;</item>
///         <item>only <c>IS_NULL</c> / <c>IS_NOT_NULL</c> filter a Secret attribute; every other operator,
///         sort, attribute search and aggregation is refused;</item>
///         <item>migration paths accept protected values and plain strings (emergency Decrypt sweep);</item>
///         <item>archive paths refuse Secret attributes.</item>
///     </list>
///     Keys are generated per test run (<see cref="RandomNumberGenerator" />); the "plaintexts" are
///     random too, so the absence check cannot pass by accident.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class SecretAttributeStorageTests(ImportTestCkModelFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SecretHolderTypeId = new("Test/SecretHolder");
    private static readonly RtCkId<CkRecordId> CredentialRecordId = new("Test/CredentialEntry");

    private readonly ISecretAttributeProtector _protector = CreateProtector();

    [Fact]
    public async Task Insert_ProtectedSecrets_StoresOnlyTheEnvelopeShape()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        var apiKeyPlain = NewPlaintext();
        var arrayPlain = NewPlaintext();
        var recordPlain = NewPlaintext();
        var rtId = OctoObjectId.GenerateNewId();

        await InsertAsync(repository, NewHolder(rtId, "protected",
            _protector.Protect(apiKeyPlain), _protector.Protect(arrayPlain), _protector.Protect(recordPlain)));

        var raw = await FindRawEntityDocumentAsync(rtId);
        Assert.NotNull(raw);
        var attributes = raw!["attributes"].AsBsonDocument;

        AssertStoredSecretShape(attributes["apiKey"]);
        AssertStoredSecretShape(attributes["credentials"][0]["attributes"]["value"]);
        AssertStoredSecretShape(attributes["primaryCredential"]["attributes"]["value"]);
        // The record key stays a plain, queryable string.
        Assert.Equal("smtp", attributes["credentials"][0]["attributes"]["key"].AsString);

        var rawBytes = raw.ToBson();
        foreach (var plaintext in new[] { apiKeyPlain, arrayPlain, recordPlain })
        {
            Assert.True(rawBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plaintext)) < 0,
                "the plaintext bytes must never reach the stored document");
        }

        // Read back through the repository: protected values, decryptable with the same key ring.
        var loaded = await LoadSingleAsync(repository, rtId);
        var apiKey = loaded.GetAttributeSecretValueOrDefault("ApiKey");
        Assert.NotNull(apiKey);
        Assert.True(apiKey!.IsProtected);
        Assert.Equal(apiKeyPlain, _protector.Unprotect(apiKey));

        var element = Assert.Single(loaded.GetRtRecordAttributeValues<RtRecord>("Credentials")!);
        Assert.Equal(arrayPlain, _protector.Unprotect(element.GetAttributeSecretValueOrDefault("Value")!));

        var primary = loaded.GetRtRecordAttributeValueOrDefault<RtRecord>("PrimaryCredential");
        Assert.Equal(recordPlain, _protector.Unprotect(primary!.GetAttributeSecretValueOrDefault("Value")!));
    }

    [Fact]
    public async Task Insert_PendingSecret_IsRefusedAndNothingIsStored()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var plaintext = NewPlaintext();
        var rtId = OctoObjectId.GenerateNewId();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            InsertAsync(repository, NewHolder(rtId, "pending", RtSecretValue.Pending(plaintext))));

        Assert.Contains(Chain(exception), e => e is SecretValueNotStorableException);
        Assert.All(Chain(exception), e => Assert.DoesNotContain(plaintext, e.Message, StringComparison.Ordinal));
        Assert.Null(await FindRawEntityDocumentAsync(rtId));
    }

    [Fact]
    public async Task Update_ProtectedSecret_IsWrittenAsSubDocument_IncludingRecordArrays()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, "update"));

        var newPlain = NewPlaintext();
        var update = new RtEntity(SecretHolderTypeId, rtId, new Dictionary<string, object?>
        {
            ["ApiKey"] = _protector.Protect(newPlain),
            ["Credentials"] = new List<RtRecord> { NewCredential("imap", _protector.Protect(NewPlaintext())) }
        });
        await ApplyUpdateAsync(repository, rtId, update);

        var attributes = (await FindRawEntityDocumentAsync(rtId))!["attributes"].AsBsonDocument;
        AssertStoredSecretShape(attributes["apiKey"]);
        AssertStoredSecretShape(attributes["credentials"][0]["attributes"]["value"]);

        var loaded = await LoadSingleAsync(repository, rtId);
        Assert.Equal(newPlain, _protector.Unprotect(loaded.GetAttributeSecretValueOrDefault("ApiKey")!));
    }

    [Fact]
    public async Task Update_PendingSecret_IsRefused()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, "update-pending", _protector.Protect(NewPlaintext())));
        var before = (await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"];

        var update = new RtEntity(SecretHolderTypeId, rtId, new Dictionary<string, object?>
        {
            ["ApiKey"] = RtSecretValue.Pending(NewPlaintext())
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ApplyUpdateAsync(repository, rtId, update));
        Assert.Contains(Chain(exception), e => e is SecretValueNotStorableException);
        Assert.Equal(before, (await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"]);
    }

    [Fact]
    public async Task Read_LegacyStringInSecretSlot_IsLegacyPlaintext()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, "legacy",
            arraySecret: _protector.Protect(NewPlaintext()), recordSecret: _protector.Protect(NewPlaintext())));

        // Data stored before the attribute became Secret: plain strings (and an enc:v1 value).
        var legacy = NewPlaintext();
        await SetRawAttributesAsync(rtId, new BsonDocument
        {
            { "attributes.apiKey", legacy },
            { "attributes.credentials.0.attributes.value", "enc:v1:legacy-format" },
            { "attributes.primaryCredential.attributes.value", "older-plaintext" }
        });

        var loaded = await LoadSingleAsync(repository, rtId);

        var apiKey = Assert.IsType<RtSecretValue>(loaded.Attributes["ApiKey"]);
        Assert.True(apiKey.IsLegacyPlaintext);
        Assert.Equal(RtSecretValue.Mask, apiKey.ToString());
        Assert.Equal(legacy, _protector.Unprotect(apiKey));

        var element = Assert.Single(loaded.GetRtRecordAttributeValues<RtRecord>("Credentials")!);
        Assert.True(Assert.IsType<RtSecretValue>(element.Attributes["Value"]).IsLegacyPlaintext);

        var primary = loaded.GetRtRecordAttributeValueOrDefault<RtRecord>("PrimaryCredential")!;
        Assert.True(Assert.IsType<RtSecretValue>(primary.Attributes["Value"]).IsLegacyPlaintext);
    }

    [Fact]
    public async Task Filter_IsNullAndIsNotNull_TreatProtectedAndLegacyValuesAsSet()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var protectedId = OctoObjectId.GenerateNewId();
        var legacyId = OctoObjectId.GenerateNewId();
        var unsetId = OctoObjectId.GenerateNewId();
        var nullId = OctoObjectId.GenerateNewId();

        await InsertAsync(repository, NewHolder(protectedId, "protected", _protector.Protect(NewPlaintext()),
            recordSecret: _protector.Protect(NewPlaintext())));
        await InsertAsync(repository, NewHolder(legacyId, "legacy"));
        await SetRawAttributesAsync(legacyId, new BsonDocument("attributes.apiKey", "legacy-value"));
        await InsertAsync(repository, NewHolder(unsetId, "unset"));
        await InsertAsync(repository, NewHolder(nullId, "null"));
        await SetRawAttributesAsync(nullId, new BsonDocument("attributes.apiKey", BsonNull.Value));

        var set = await QueryIdsAsync(repository,
            RtEntityQueryOptions.Create().FieldFilter("ApiKey", FieldFilterOperator.IsNotNull, null));
        var notSet = await QueryIdsAsync(repository,
            RtEntityQueryOptions.Create().FieldFilter("apiKey", FieldFilterOperator.IsNull, null));
        // The comparison value is ignored for the two allowed operators - never converted, never rendered.
        var setIgnoringValue = await QueryIdsAsync(repository,
            RtEntityQueryOptions.Create().FieldFilter("ApiKey", FieldFilterOperator.IsNotNull, "ignored"));
        var recordSet = await QueryIdsAsync(repository,
            RtEntityQueryOptions.Create().FieldFilter("PrimaryCredential.Value", FieldFilterOperator.IsNotNull, null));

        Assert.Equal(new[] { protectedId, legacyId }.Order(), set.Order());
        Assert.Equal(new[] { unsetId, nullId }.Order(), notSet.Order());
        Assert.Equal(set.Order(), setIgnoringValue.Order());
        Assert.Equal([protectedId], recordSet);
    }

    public static TheoryData<string, FieldFilterOperator> RefusedFilters() => new()
    {
        { "ApiKey", FieldFilterOperator.Equals },
        { "ApiKey", FieldFilterOperator.NotEquals },
        { "ApiKey", FieldFilterOperator.Like },
        { "ApiKey", FieldFilterOperator.StartsWith },
        { "ApiKey", FieldFilterOperator.MatchRegEx },
        { "ApiKey", FieldFilterOperator.In },
        { "ApiKey", FieldFilterOperator.GreaterThan },
        { "PrimaryCredential.Value", FieldFilterOperator.Equals },
        { "Credentials.Value", FieldFilterOperator.AnyEq }
    };

    [Theory]
    [MemberData(nameof(RefusedFilters))]
    public async Task Filter_AnyOtherOperator_IsRefused(string attributePath, FieldFilterOperator filterOperator)
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var probe = NewPlaintext();

        var exception = await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() =>
            QueryIdsAsync(repository, RtEntityQueryOptions.Create().FieldFilter(attributePath, filterOperator, probe)));

        Assert.Equal(attributePath, exception.AttributePath);
        Assert.DoesNotContain(probe, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_SecretSubAttributeInsideRecordArrayMatch_IsRefused()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var nested = FieldFilterCriteria.Create().Field("Value", FieldFilterOperator.Equals, "probe");

        await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() =>
            QueryIdsAsync(repository, RtEntityQueryOptions.Create().FieldFilter("Credentials", FieldFilterOperator.Match, nested)));
    }

    [Fact]
    public async Task Sort_AttributeSearch_AndAggregations_AreRefused()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();

        await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() =>
            QueryIdsAsync(repository, RtEntityQueryOptions.Create().SortOrder("ApiKey", SortOrders.Ascending)));
        await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() =>
            QueryIdsAsync(repository, RtEntityQueryOptions.Create().SortOrder("PrimaryCredential.Value", SortOrders.Descending)));
        await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() =>
            QueryIdsAsync(repository, RtEntityQueryOptions.Create().AttributeSearch(["Name", "ApiKey"], "probe")));

        var groupBy = RtEntityQueryOptions.Create();
        groupBy.AggregateFieldGroupBy("ApiKey");
        await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() => QueryIdsAsync(repository, groupBy));

        var count = RtEntityQueryOptions.Create();
        count.AggregateResult().CountAttributePaths("ApiKey");
        await Assert.ThrowsAsync<SecretAttributeNotQueryableException>(() => QueryIdsAsync(repository, count));

        // Control: the non-secret siblings stay fully queryable.
        await QueryIdsAsync(repository, RtEntityQueryOptions.Create()
            .FieldFilter("Name", FieldFilterOperator.Equals, "x")
            .SortOrder("Name", SortOrders.Ascending));
    }

    [Fact]
    public async Task MigrationPaths_AcceptProtectedValuesAndPlainStrings()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        var insertedPlain = NewPlaintext();

        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityForMigrationAsync(session, SecretHolderTypeId,
                NewHolder(rtId, "migration", _protector.Protect(insertedPlain)));
            await session.CommitTransactionAsync();
        }

        AssertStoredSecretShape((await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"]);

        // Re-protect sweep: the migration rewrite writes a protected value as a sub-document ...
        var rewrittenPlain = NewPlaintext();
        await RewriteAsync(repository, rtId, _protector.Protect(rewrittenPlain));
        var stored = (await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"];
        AssertStoredSecretShape(stored);
        Assert.Equal(rewrittenPlain, _protector.Unprotect(stored["e"].AsString));

        // ... and the emergency Decrypt sweep writes a plain string, read back as legacy.
        await RewriteAsync(repository, rtId, rewrittenPlain);
        Assert.Equal(BsonType.String, (await FindRawEntityDocumentAsync(rtId))!["attributes"]["apiKey"].BsonType);
        var loaded = await LoadSingleAsync(repository, rtId);
        Assert.True(loaded.GetAttributeSecretValueOrDefault("ApiKey")!.IsLegacyPlaintext);

        // A pending value is refused here as well.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            RewriteAsync(repository, rtId, RtSecretValue.Pending(NewPlaintext())));
        Assert.Contains(Chain(exception), e => e is SecretValueNotStorableException);
    }

    [Fact]
    public async Task Read_LegacyStringInANavigationTarget_IsLegacyPlaintext()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        // A SecretHolder reached through a navigation (Customer -References-> System/Entity) carries a
        // legacy string in its Secret slot: the graph item's navigation target must be normalised
        // like a root entity and never hand the plaintext out as a string.
        var targetId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(targetId, "navigation-target"));
        var legacy = NewPlaintext();
        await SetRawAttributesAsync(targetId, new BsonDocument("attributes.apiKey", legacy));

        var originId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, new RtEntity(TestCkIds.RtCkCustomerTypeId, originId,
            new Dictionary<string, object?>
            {
                ["Name"] = new RtRecord(new RtCkId<CkRecordId>("Test/ContactName"),
                    new Dictionary<string, object?> { ["LastName"] = "navigation-origin" })
            }));

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, new List<IEntityUpdateInfo<RtEntity>>(),
            [
                AssociationUpdateInfo.CreateInsert(new RtEntityId(TestCkIds.RtCkCustomerTypeId, originId),
                    new RtEntityId(SecretHolderTypeId, targetId), new RtCkId<CkAssociationRoleId>("Test/References"))
            ],
            operationResult);
        Assert.False(operationResult.HasErrors);

        var tenantId = fixture.GetSystemContext().TenantId;
        var referencesAssociation = fixture.GetService<ICkCacheService>()
            .GetRtCkType(tenantId, TestCkIds.RtCkCustomerTypeId).Associations.Out.All
            .First(a => a.NavigationPropertyName == "References");
        var pair = new NavigationPair(
            [
                new PathTerm("References", PathType.Navigation),
                new PathTerm("Entity", PathType.TargetCkTypeId)
            ],
            [],
            referencesAssociation.CkRoleId.ToRtCkId(),
            GraphDirections.Outbound,
            new RtCkId<CkTypeId>("System/Entity"));

        var result = await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create().FieldFilter("RtId", FieldFilterOperator.Equals, originId.ToString()),
            [pair]);
        await session.CommitTransactionAsync();

        var origin = Assert.Single(result.Items);
        var target = Assert.Single(Assert.Single(origin.Associations).Targets);
        Assert.Equal(targetId, target.RtId);
        var apiKey = Assert.IsType<RtSecretValue>(target.Attributes["ApiKey"]);
        Assert.True(apiKey.IsLegacyPlaintext);
        Assert.Equal(legacy, _protector.Unprotect(apiKey));
    }

    [Fact]
    public void ArchivePaths_RefuseSecretAttributes()
    {
        var cache = fixture.GetService<ICkCacheService>();
        var tenantId = fixture.GetSystemContext().TenantId;

        foreach (var path in new[] { "ApiKey", "PrimaryCredential.Value", "Credentials.Value" })
        {
            Assert.Throws<UnresolvableArchivePathException>(() => ArchivePathTypeResolver.Resolve(cache, tenantId,
                SecretHolderTypeId, [new CkArchiveColumnSpec(path, Indexed: false, Required: false)]));
        }

        // A record picked as a whole is archived without its Secret sub-attribute.
        var column = Assert.Single(ArchivePathTypeResolver.Resolve(cache, tenantId, SecretHolderTypeId,
            [new CkArchiveColumnSpec("PrimaryCredential", Indexed: false, Required: false)]));
        var rendered = column.Type.Render();
        Assert.Contains("\"Key\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Value\"", rendered, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static ISecretAttributeProtector CreateProtector()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.Configure<SecretEncryptionOptions>(options =>
        {
            options.Keys["t1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            options.ActiveKeyId = "t1";
        });
        return services.BuildServiceProvider().GetRequiredService<ISecretAttributeProtector>();
    }

    private static string NewPlaintext() => "pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static RtRecord NewCredential(string key, RtSecretValue? value) =>
        new(CredentialRecordId, new Dictionary<string, object?> { ["Key"] = key, ["Value"] = value });

    private static RtEntity NewHolder(OctoObjectId rtId, string name, RtSecretValue? apiKey = null,
        RtSecretValue? arraySecret = null, RtSecretValue? recordSecret = null)
    {
        var attributes = new Dictionary<string, object?> { ["Name"] = name };
        if (apiKey != null)
        {
            attributes["ApiKey"] = apiKey;
        }

        if (arraySecret != null)
        {
            attributes["Credentials"] = new List<RtRecord> { NewCredential("smtp", arraySecret) };
        }

        if (recordSecret != null)
        {
            attributes["PrimaryCredential"] = NewCredential("main", recordSecret);
        }

        return new RtEntity(SecretHolderTypeId, rtId, attributes);
    }

    private static void AssertStoredSecretShape(BsonValue value)
    {
        var document = Assert.IsType<BsonDocument>(value);
        Assert.Equal(2, document.ElementCount);
        Assert.Equal("OctoSecret", document["_t"].AsString);
        var envelope = document["e"].AsString;
        Assert.StartsWith(SecretEnvelope.PrefixV2 + "t1:", envelope, StringComparison.Ordinal);
        Assert.True(SecretEnvelope.IsEnvelope(envelope));
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            yield return current;
        }
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

    private static async Task ApplyUpdateAsync(ITenantRepository repository, OctoObjectId rtId, RtEntity update)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            var operationResult = new OperationResult();
            await repository.ApplyChangesAsync(session,
                [EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(SecretHolderTypeId, rtId), update)],
                operationResult);
            Assert.False(operationResult.HasErrors);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    private static async Task RewriteAsync(ITenantRepository repository, OctoObjectId rtId, object? value)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            await repository.RewriteAttributeValueForMigrationAsync(session, SecretHolderTypeId, rtId, "ApiKey", value);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    private static async Task<RtEntity> LoadSingleAsync(ITenantRepository repository, OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByTypeAsync(session, SecretHolderTypeId.FullName,
            RtEntityQueryOptions.Create().FieldFilter("RtId", FieldFilterOperator.Equals, rtId.ToString()));
        await session.CommitTransactionAsync();
        return Assert.Single(result.Items);
    }

    private static async Task<List<OctoObjectId>> QueryIdsAsync(ITenantRepository repository,
        RtEntityQueryOptions options)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            var result = await repository.GetRtEntitiesByTypeAsync(session, SecretHolderTypeId.FullName, options);
            await session.CommitTransactionAsync();
            return result.Items.Select(e => e.RtId).ToList();
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
