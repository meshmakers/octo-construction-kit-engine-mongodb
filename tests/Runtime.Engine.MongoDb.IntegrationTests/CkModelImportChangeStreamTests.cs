using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests pinning the MongoDB <c>changeStreamPreAndPostImages</c> collection
/// option to the CK type's <c>EnableChangeStreamPreAndPostImages</c> YAML flag.
///
/// Regression coverage for the AB#2767 follow-up gap: <c>UpdateCollectionsAsync</c>
/// filtered by <c>ModelState.Available</c>, which excluded the just-inserted <c>Importing</c>
/// CkTypes during import — so <c>CreateCollectionIfNotExistsAsync</c> was never called
/// for them and MongoDB auto-created the <c>RtEntity_*</c> collection without the
/// <c>changeStreamPreAndPostImages</c> option applied.
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkModelImportChangeStreamTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");

    [Fact]
    public async Task Import_sets_changeStreamPreAndPostImages_when_ck_type_flag_is_true()
    {
        // Arrange: clean tenant.
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();

        // Act: import Test-1.0.0. WatchTarget declares enableChangeStreamPreAndPostImages: true
        // and is a collection root (derives from ${System}/Entity).
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        operationResult.HasErrors.Should().BeFalse("import must succeed before we can assert on collection options");

        // Assert: the RtEntity collection for WatchTarget has changeStreamPreAndPostImages enabled.
        var db = GetSystemMongoDatabase();
        var watchTargetCollection = await FindRtEntityCollectionByTypeNameAsync(db, "WatchTarget");

        watchTargetCollection.Should().NotBeNull(
            "WatchTarget is a non-abstract collection root, so UpdateCollectionsAsync must have created its RtEntity_* collection. Existing RtEntity_* collections: {0}",
            await ListRtEntityCollectionNamesAsync(db));
        GetPreAndPostImagesEnabled(watchTargetCollection!).Should().BeTrue(
            "WatchTarget.yaml sets enableChangeStreamPreAndPostImages: true — the MongoDB collection option must reflect that");
    }

    [Fact]
    public async Task Import_reconciles_changeStreamPreAndPostImages_when_collection_option_drifted()
    {
        // Arrange: clean tenant and import v1 so WatchTarget's collection exists with the flag on.
        // We cannot re-import the same version to test reconciliation because `ExecuteImport`
        // short-circuits when the exact CK model version is already Available (see
        // DatabaseCkModelRepository.cs:420). Instead, we drift the option and import v2; v2's
        // import runs UpdateCollectionsAsync at line 480 *before* DeletePreviousVersion, so v1's
        // WatchTarget — still Available at that moment — is iterated and reconciled.
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();

        var firstImport = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, firstImport);
        firstImport.HasErrors.Should().BeFalse("v1 import must succeed before we can drift the option");

        var db = GetSystemMongoDatabase();
        var watchTargetBefore = await FindRtEntityCollectionByTypeNameAsync(db, "WatchTarget");
        watchTargetBefore.Should().NotBeNull();
        GetPreAndPostImagesEnabled(watchTargetBefore!).Should().BeTrue(
            "baseline: Phase 1 fix must have applied changeStreamPreAndPostImages on v1 import");

        // Simulate drift: some external actor (ops, backup restore, pre-fix build) leaves the
        // collection with changeStreamPreAndPostImages disabled even though the CK type says it
        // should be on.
        var collectionName = watchTargetBefore!["name"].AsString;
        await RunCollModChangeStreamPreAndPostImagesAsync(db, collectionName, enabled: false);

        var driftedState = await FindRtEntityCollectionByTypeNameAsync(db, "WatchTarget");
        GetPreAndPostImagesEnabled(driftedState!).Should().BeFalse(
            "sanity check: collMod must have flipped the option to false");

        // Act: import v2 of the Test model. v2 does not itself declare WatchTarget, but its
        // import flow visits v1's still-Available WatchTarget via the first UpdateCollectionsAsync
        // call — which is where reconciliation runs.
        var v2Import = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV2ModelId, v2Import);
        v2Import.HasErrors.Should().BeFalse("v2 import must succeed");

        // Assert: the flag is restored on the pre-existing collection.
        var watchTargetAfter = await FindRtEntityCollectionByTypeNameAsync(db, "WatchTarget");
        watchTargetAfter.Should().NotBeNull(
            "WatchTarget's collection must still exist after v2 import (it was protected by validCollectionSuffixes during cleanup)");
        GetPreAndPostImagesEnabled(watchTargetAfter!).Should().BeTrue(
            "import must reconcile the existing collection's changeStreamPreAndPostImages option to match the CK type's EnableChangeStreamPreAndPostImages flag");
    }

    private static async Task RunCollModChangeStreamPreAndPostImagesAsync(
        IMongoDatabase db, string collectionName, bool enabled)
    {
        var command = new BsonDocument
        {
            { "collMod", collectionName },
            {
                "changeStreamPreAndPostImages", new BsonDocument("enabled", enabled)
            }
        };
        await db.RunCommandAsync<BsonDocument>(command);
    }

    [Fact]
    public async Task Import_leaves_changeStreamPreAndPostImages_disabled_when_ck_type_flag_is_false()
    {
        // Arrange: clean tenant.
        await fixture.ResetTenantAsync();
        var systemContext = fixture.GetSystemContext();

        // Act: import Test-1.0.0. WatchTargetNoPreImage is a collection root WITHOUT the flag.
        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(TestV1ModelId, operationResult);
        operationResult.HasErrors.Should().BeFalse("import must succeed before we can assert on collection options");

        // Assert: the RtEntity collection for WatchTargetNoPreImage does not have pre-image capture.
        var db = GetSystemMongoDatabase();
        var noPreImageCollection = await FindRtEntityCollectionByTypeNameAsync(db, "WatchTargetNoPreImage");

        noPreImageCollection.Should().NotBeNull(
            "WatchTargetNoPreImage must have its RtEntity_* collection created. Existing RtEntity_* collections: {0}",
            await ListRtEntityCollectionNamesAsync(db));
        GetPreAndPostImagesEnabled(noPreImageCollection!).Should().BeFalse(
            "WatchTargetNoPreImage.yaml does not set enableChangeStreamPreAndPostImages — the option must stay off");
    }

    /// <summary>
    /// Finds the <c>RtEntity_*</c> collection whose suffix corresponds to the given CK type name.
    /// <c>GetCkTypeCollectionName</c> strips non-alphanumerics from <c>SemanticVersionedFullName</c>.
    /// For v1 types that suffix is bare (no version), for v2+ it has a trailing version number —
    /// e.g. <c>RtEntity_TestWatchTarget</c> (v1) or <c>RtEntity_TestWatchTarget2</c> (v2).
    /// Anchoring as <c>{TypeName}\d*$</c> matches both forms while still distinguishing
    /// <c>WatchTarget</c> from <c>WatchTargetNoPreImage</c>.
    /// </summary>
    private static async Task<BsonDocument?> FindRtEntityCollectionByTypeNameAsync(IMongoDatabase db, string ckTypeName)
    {
        var filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity_"));
        using var cursor = await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        var docs = await cursor.ToListAsync();

        var typeNameAtEnd = new System.Text.RegularExpressions.Regex(
            $@"{System.Text.RegularExpressions.Regex.Escape(ckTypeName)}\d*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return docs.FirstOrDefault(d => typeNameAtEnd.IsMatch(d["name"].AsString));
    }

    /// <summary>
    /// Lists all <c>RtEntity_*</c> collections — used to build helpful failure messages when
    /// the expected collection is missing.
    /// </summary>
    private static async Task<string> ListRtEntityCollectionNamesAsync(IMongoDatabase db)
    {
        var filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity_"));
        using var cursor = await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        var docs = await cursor.ToListAsync();
        var names = docs.Select(d => d["name"].AsString).OrderBy(n => n).ToList();
        return names.Count == 0 ? "(no RtEntity_* collections)" : string.Join(", ", names);
    }

    private static bool GetPreAndPostImagesEnabled(BsonDocument collectionInfo)
    {
        if (!collectionInfo.Contains("options")) return false;
        var options = collectionInfo["options"].AsBsonDocument;
        if (!options.Contains("changeStreamPreAndPostImages")) return false;
        var cs = options["changeStreamPreAndPostImages"].AsBsonDocument;
        return cs.GetValue("enabled", BsonBoolean.False).AsBoolean;
    }

    private IMongoDatabase GetSystemMongoDatabase()
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

        var urlBuilder = new MongoUrlBuilder
        {
            Server = new MongoServerAddress(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection,
        };

        var client = new MongoClient(urlBuilder.ToMongoUrl());
        return client.GetDatabase(config.SystemDatabaseName);
    }
}
