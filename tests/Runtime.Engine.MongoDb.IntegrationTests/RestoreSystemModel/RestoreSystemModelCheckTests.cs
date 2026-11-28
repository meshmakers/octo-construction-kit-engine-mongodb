using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

using static Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RestoreSystemModel.RestoreSystemModelFixture;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RestoreSystemModel;

/// <summary>
///     The restore check of service-managed models against a real MongoDB with real mongodump/mongorestore: a dump is
///     taken of a source tenant whose System.Identity rows the test prepared (installed or edited in the database before
///     the dump), and restored under another tenant id and database name (namespace mapping) or over the source itself.
///     A refused restore attaches nothing, creates no database user and leaves no restored database behind; an existing
///     target stays as it was.
/// </summary>
[Collection(RestoreSystemModelCollection.Name)]
public sealed class RestoreSystemModelCheckTests(RestoreSystemModelFixture fixture) : IAsyncDisposable
{
    private static readonly RtCkId<CkTypeId> UserTypeId = new("System.Identity/User");
    private readonly List<string> _tenants = [];
    private readonly List<string> _files = [];
    private MongoClient? _client;

    private ISystemContext Sys => fixture.GetSystemContext();

    [Fact]
    public async Task GenuineDumpOfThePlatformsVersion_RestoresUnchanged_AndTheRestrictionHolds()
    {
        var source = await SourceTenantAsync("genuine", await fixture.Catalog.LoadAsync(Identity380));
        var archive = await DumpAsync(source);
        var target = NewTenantId("genuinert");

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        Assert.True(result.Success, result.Error);
        Assert.Equal(await SnapshotAsync(source, IdentityName), await SnapshotAsync(target, IdentityName));
        Assert.Contains(Identity380.FullName, await ModelIdsAsync(target));
        await AssertGenericUserWriteRefusedAsync(await ResolveAsync(target));
    }

    [Fact]
    public async Task GenuineDump_RestoredOverItsOwnTenant_RestoresUnchanged()
    {
        var source = await SourceTenantAsync("overown", await fixture.Catalog.LoadAsync(Identity380));
        var before = await SnapshotAsync(source, IdentityName);
        var archive = await DumpAsync(source);

        var result = await Sys.RestoreTenantAsync(source, source, archive);

        Assert.True(result.Success, result.Error);
        Assert.Equal(before, await SnapshotAsync(source, IdentityName));
    }

    public static TheoryData<string> Manipulations => new()
    {
        "writeAccess removed from the element rows",
        "model row changed",
        "element row filed under another model",
        "model row of another id named System.Identity",
        "extra inheritance row"
    };

    [Theory]
    [MemberData(nameof(Manipulations))]
    public async Task ManipulatedDump_IsRefusedBeforeTheAttach_AndLeavesNothingBehind(string manipulation)
    {
        var source = await SourceTenantAsync("manip", await fixture.Catalog.LoadAsync(Identity380));
        await ManipulateAsync(source, manipulation);
        var archive = await DumpAsync(source);
        var target = NewTenantId("maniprt");
        using var counters = new CkCounterRecorder();

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        var expectedReason = manipulation is "writeAccess removed from the element rows" or "model row changed"
            ? "content_differs"
            : "inconsistent_rows";
        await AssertRefusedAndNothingLeftAsync(result, target, expectedReason);
        Assert.True(counters.Sum(CkModelImportDiagnostics.RestoreSystemModelRefusedCounterName,
            ("reason", expectedReason)) >= 1);
    }

    [Fact]
    public async Task ManipulatedDump_RestoredOverAnExistingTenant_LeavesThatTenantUnchanged()
    {
        var existing = await SourceTenantAsync("keep", await fixture.Catalog.LoadAsync(Identity380));
        var before = await SnapshotAsync(existing, IdentityName);
        var manipulated = await SourceTenantAsync("keepsrc", await fixture.Catalog.LoadAsync(Identity380));
        await ManipulateAsync(manipulated, "writeAccess removed from the element rows");
        var archive = await DumpAsync(manipulated);

        var result = await Sys.RestoreTenantAsync(existing, existing, archive, sourceDatabaseName: manipulated,
            dropExistingTenant: true);

        Assert.False(result.Success);
        Assert.StartsWith(RestoreSystemModelGuard.RefusalCode, result.Error, StringComparison.Ordinal);
        Assert.True(await TenantExistsAsync(existing), "the existing tenant must not be dropped by a refused restore");
        Assert.Equal(before, await SnapshotAsync(existing, IdentityName));
        await AssertGenericUserWriteRefusedAsync(await ResolveAsync(existing));
    }

    [Theory]
    [InlineData("3.99.0", "unknown_version")]
    [InlineData("4.0.0", "higher_major")]
    public async Task UnpublishedSystemIdentityVersion_IsRefused(string version, string reason)
    {
        var fake = await fixture.Catalog.LoadAsync(new CkModelId(IdentityName, version), WithoutWriteAccess);
        var source = await SourceTenantAsync("fake", fake);
        var archive = await DumpAsync(source);
        var target = NewTenantId("fakert");

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        await AssertRefusedAndNothingLeftAsync(result, target, reason);
        Assert.Contains($"{IdentityName}-{version}", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeVersion_RestoredOverAnExistingTenantWithoutDrop_IsRefused_AndTheTenantStaysAsItWas()
    {
        var existing = await SourceTenantAsync("fakekeep", await fixture.Catalog.LoadAsync(Identity380));
        var before = await SnapshotAsync(existing, IdentityName);
        var fake = await SourceTenantAsync("fakekeepsrc",
            await fixture.Catalog.LoadAsync(new CkModelId(IdentityName, "3.99.0"), WithoutWriteAccess));
        var archive = await DumpAsync(fake);

        var result = await Sys.RestoreTenantAsync(existing, existing, archive, sourceDatabaseName: fake,
            dropExistingTenant: false);

        Assert.False(result.Success);
        Assert.StartsWith(RestoreSystemModelGuard.RefusalCode, result.Error, StringComparison.Ordinal);
        Assert.Contains("(unknown_version)", result.Error, StringComparison.Ordinal);
        Assert.True(await TenantExistsAsync(existing));
        Assert.Equal(before, await SnapshotAsync(existing, IdentityName));
    }

    [Fact]
    public async Task UnknownServiceManagedModel_IsRefused()
    {
        var source = await SourceTenantAsync("foo", await fixture.Catalog.LoadAsync(Identity380));
        await InstallAsync(source, SmallModel(new CkModelId("System.Foo", "1.0.0"), await SystemIdAsync(source)));
        var archive = await DumpAsync(source);
        var target = NewTenantId("foort");

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        await AssertRefusedAndNothingLeftAsync(result, target, "unknown_model");
    }

    [Fact]
    public async Task GenuineOlderDump_IsRaisedToThePlatformsVersion_AndTheRestrictionHoldsFromTheFirstRequest()
    {
        var source = await SourceTenantAsync("older", await fixture.Catalog.LoadAsync(Identity370, WithoutWriteAccess));
        var archive = await DumpAsync(source);
        var target = NewTenantId("olderrt");
        using var counters = new CkCounterRecorder();

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        Assert.True(result.Success, result.Error);
        var models = await ModelIdsAsync(target);
        Assert.Contains(Identity380.FullName, models);
        Assert.DoesNotContain(Identity370.FullName, models);
        Assert.Equal(await SnapshotOfPublishedAsync(Identity380), await ReadBackCanonicalAsync(target, Identity380));
        Assert.True(counters.Sum(CkModelImportDiagnostics.RestoreSystemModelRaisedCounterName,
            ("model", IdentityName)) >= 1);
        Assert.True(await UserExistsAsync(target));
        await AssertGenericUserWriteRefusedAsync(await ResolveAsync(target));
    }

    [Fact]
    public async Task GenuineOlderDump_RestoredWithoutAttach_IsRaised_AndTheUserTheCheckCreatedIsDroppedAgain()
    {
        var source = await SourceTenantAsync("olderna", await fixture.Catalog.LoadAsync(Identity370, WithoutWriteAccess));
        var archive = await DumpAsync(source);
        var target = NewTenantId("oldernart");

        var result = await Sys.RestoreTenantAsync(target, target, archive, attachTenant: false);

        Assert.True(result.Success, result.Error);
        Assert.False(await TenantExistsAsync(target));
        Assert.Contains(Identity380.FullName, await ModelIdsAsync(target));
        Assert.False(await UserExistsAsync(target), "a user the check created for the raise must not outlive it");
        await DropDatabaseAsync(target);
    }

    [Fact]
    public async Task OlderDumpWithOtherContentThanThePublishedOlderVersion_IsRefused_NotRaised()
    {
        var source = await SourceTenantAsync("olddiff", await fixture.Catalog.LoadAsync(Identity370, model =>
        {
            WithoutWriteAccess(model);
            model.Description = "self-built";
        }));
        var archive = await DumpAsync(source);
        var target = NewTenantId("olddiffrt");

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        await AssertRefusedAndNothingLeftAsync(result, target, "content_differs");
    }

    [Fact]
    public async Task CatalogUnreachable_FailsClosed()
    {
        var source = await SourceTenantAsync("nocat", await fixture.Catalog.LoadAsync(Identity380));
        var archive = await DumpAsync(source);
        var target = NewTenantId("nocatrt");
        CommandResult result;
        fixture.Catalog.Unreachable = true;
        try
        {
            result = await Sys.RestoreTenantAsync(target, target, archive);
        }
        finally
        {
            fixture.Catalog.Unreachable = false;
        }

        await AssertRefusedAndNothingLeftAsync(result, target, "catalog_unavailable");
    }

    [Fact]
    public async Task SystemTenantUnreadable_FailsClosed()
    {
        var source = await SourceTenantAsync("nosys", await fixture.Catalog.LoadAsync(Identity380));
        var archive = await DumpAsync(source);
        var target = NewTenantId("nosysrt");
        var systemModels = Database(Sys.DatabaseName).GetCollection<BsonDocument>("CkModel");
        var broken = new BsonDocument { { "_id", "System.Broken-1.0.0" }, { "modelId", new BsonArray { 42 } } };
        await systemModels.InsertOneAsync(broken, cancellationToken: TestContext.Current.CancellationToken);
        CommandResult result;
        try
        {
            result = await Sys.RestoreTenantAsync(target, target, archive);
        }
        finally
        {
            await systemModels.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", "System.Broken-1.0.0"),
                TestContext.Current.CancellationToken);
        }

        await AssertRefusedAndNothingLeftAsync(result, target, "system_tenant_unavailable");
    }

    [Fact]
    public async Task ParallelRestores_OneRefused_OneRaised_DoNotSeeEachOther()
    {
        var bystander = await SourceTenantAsync("bystand", await fixture.Catalog.LoadAsync(Identity380));
        var manipulated = await SourceTenantAsync("parbad", await fixture.Catalog.LoadAsync(Identity380));
        await ManipulateAsync(manipulated, "writeAccess removed from the element rows");
        var older = await SourceTenantAsync("parold", await fixture.Catalog.LoadAsync(Identity370, WithoutWriteAccess));
        var badArchive = await DumpAsync(manipulated);
        var oldArchive = await DumpAsync(older);
        var badTarget = NewTenantId("parbadrt");
        var oldTarget = NewTenantId("paroldrt");

        var results = await Task.WhenAll(
            Task.Run(() => Sys.RestoreTenantAsync(badTarget, badTarget, badArchive)),
            Task.Run(() => Sys.RestoreTenantAsync(oldTarget, oldTarget, oldArchive)));

        await AssertRefusedAndNothingLeftAsync(results[0], badTarget, "content_differs");
        Assert.True(results[1].Success, results[1].Error);
        Assert.Contains(Identity380.FullName, await ModelIdsAsync(oldTarget));
        Assert.True(await UserExistsAsync(oldTarget));
        await AssertGenericUserWriteRefusedAsync(await ResolveAsync(oldTarget));
        Assert.True(await TenantExistsAsync(bystander));
        Assert.True(await UserExistsAsync(bystander));
        Assert.False(RestoringTenantScope.TryGetDatabaseName(oldTarget, out _));
    }

    [Fact]
    public async Task RestoreScope_ResolvesOnlyTheRestoringTenant_ThroughTheMigrationsRepositoryProvider()
    {
        var other = await SourceTenantAsync("scopeoth", await fixture.Catalog.LoadAsync(Identity380));
        var provider = fixture.Provider!.GetRequiredService<IRuntimeRepositoryProvider>();
        var restoring = NewTenantId("scoperst");

        Assert.Null(await provider.GetRepositoryAsync(restoring, TestContext.Current.CancellationToken));
        using (RestoringTenantScope.Begin(restoring, other))
        {
            Assert.NotNull(await provider.GetRepositoryAsync(restoring, TestContext.Current.CancellationToken));
            var resolvedOther = await Sys.TryFindTenantContextAsync(other);
            Assert.Equal(other, resolvedOther!.DatabaseName);
        }

        Assert.Null(await provider.GetRepositoryAsync(restoring, TestContext.Current.CancellationToken));
    }

    // ---- resolved dependency closure (AB#10061) ------------------------------------------------------------------

    /// <summary>
    ///     Regression test of AB#10061: a genuine dump of a fresh tenant with every service-managed model this repository
    ///     can install - System, the engine's embedded System.StreamData, System.Identity, and the published System.UI,
    ///     System.Notification and System.Files files - restores unchanged although each model's resolved dependency
    ///     closure (the services' build: the tenant's System) differs from the catalog's (its first publish: System-2.8.0,
    ///     System.Files System-2.5.0), exactly the skew of the published catalog. Fails at ac936506 (content_differs for
    ///     every one of them).
    /// </summary>
    [Fact]
    public async Task GenuineDumpOfAFreshTenant_ClosuresOfAnotherBuildThanThePublishedOnes_RestoresUnchanged()
    {
        await WithPublishedClosureSkewAsync(async (source, names) =>
        {
            var archive = await DumpAsync(source);
            var target = NewTenantId("skewrt");

            var result = await Sys.RestoreTenantAsync(target, target, archive);

            Assert.True(result.Success, result.Error);
            Assert.Equal((await ModelIdsAsync(source)).Order(), (await ModelIdsAsync(target)).Order());
            foreach (var name in names)
            {
                Assert.Equal(await SnapshotAsync(source, name), await SnapshotAsync(target, name));
            }

            await AssertGenericUserWriteRefusedAsync(await ResolveAsync(target));
        });
    }

    public static TheoryData<string, string> ClosureManipulations => new()
    {
        { "closure entry of an unpublished version inside the range", "dependency_unpublished" },
        { "closure entry of another major", "content_differs" },
        { "closure entry added", "content_differs" },
        { "declared floor lowered", "content_differs" },
        { "writeAccess removed next to a moved closure", "content_differs" }
    };

    [Theory]
    [MemberData(nameof(ClosureManipulations))]
    public async Task DumpWithAManipulatedClosureOrContent_IsRefused_AndLeavesNothingBehind(string manipulation,
        string reason)
    {
        await WithPublishedClosureSkewAsync(async (source, _) =>
        {
            await ManipulateClosureAsync(source, manipulation);
            var archive = await DumpAsync(source);
            var target = NewTenantId("skewbad");

            var result = await Sys.RestoreTenantAsync(target, target, archive);

            await AssertRefusedAndNothingLeftAsync(result, target, reason);
            if (reason == "dependency_unpublished")
            {
                Assert.Contains("'System-2.99.0'", result.Error, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task GenuineOlderDump_WithTheClosureOfAnotherBuild_IsRaisedToThePlatformsVersion()
    {
        var older = await fixture.Catalog.LoadAsync(Identity370, WithoutWriteAccess);
        var source = NewTenantId("skewold");
        await CreateTenantAsync(source);
        await InstallAsync(source, WithClosureOf(older, await SystemIdAsync(source)));
        var archive = await DumpAsync(source);
        var target = NewTenantId("skewoldrt");

        var result = await Sys.RestoreTenantAsync(target, target, archive);

        Assert.True(result.Success, result.Error);
        var models = await ModelIdsAsync(target);
        Assert.Contains(Identity380.FullName, models);
        Assert.DoesNotContain(Identity370.FullName, models);
        await AssertGenericUserWriteRefusedAsync(await ResolveAsync(target));
    }

    [Fact]
    public async Task GenuineOlderDump_WhenThePlatformsVersionHasAnUnpublishedClosureEntry_IsRefused_NotRaised()
    {
        var source = await SourceTenantAsync("skewtgt", await fixture.Catalog.LoadAsync(Identity370, WithoutWriteAccess));
        var archive = await DumpAsync(source);
        var target = NewTenantId("skewtgtrt");
        var systemModels = Database(Sys.DatabaseName).GetCollection<BsonDocument>("CkModel");
        var platformRow = Builders<BsonDocument>.Filter.Eq("_id", Identity380.FullName);
        var closure = (await systemModels.Find(platformRow).FirstAsync(TestContext.Current.CancellationToken))["dependencies"];
        CommandResult result;
        await systemModels.UpdateOneAsync(platformRow,
            Builders<BsonDocument>.Update.Set("dependencies", new BsonArray { "System-2.99.0" }),
            cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            result = await Sys.RestoreTenantAsync(target, target, archive);
        }
        finally
        {
            await systemModels.UpdateOneAsync(platformRow, Builders<BsonDocument>.Update.Set("dependencies", closure),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await AssertRefusedAndNothingLeftAsync(result, target, "target_not_published");
    }

    /// <summary>
    ///     Runs <paramref name="test" /> with a source tenant whose service-managed models carry the closure of a service
    ///     build (the tenant's own System), while the test catalog publishes them with the closures of the real catalog;
    ///     the catalog is put back afterwards. Passes the names of the service-managed models of the source tenant.
    /// </summary>
    private async Task WithPublishedClosureSkewAsync(Func<string, IReadOnlyList<string>, Task> test)
    {
        var withdraw = new List<CkModelId>();
        try
        {
            var source = NewTenantId("skew");
            await CreateTenantAsync(source);
            var system = await SystemIdAsync(source);

            // The engine's embedded System.StreamData, installed as the service embeds it (built against the tenant's
            // System) and published with the closure of the real catalog.
            var catalogService = fixture.GetService<ICatalogService>();
            var found = await catalogService.IsExistingAsync(new CkModelIdVersionRange("System.StreamData", "0.0.1"));
            Assert.True(found is { Exists: true, ModelId: not null }, "the engine embeds System.StreamData");
            var streamData = await catalogService.GetAsync(found.ModelId!, new OperationResult());
            Assert.NotNull(streamData);
            Assert.True(streamData.IsRangeRetaining, "System.StreamData must be range-retaining like the published one");
            Assert.Equal([system], streamData.Dependencies);
            fixture.Catalog.PublishFile(streamData.ModelId, await JsonOfAsync(streamData),
                m => m.Dependencies = [new CkModelId("System-2.8.0")]);
            withdraw.Add(streamData.ModelId);
            await InstallAsync(source, streamData);

            // The published files of System.UI, System.Notification and System.Files (and System.Identity), installed
            // as a service build of today would: with the tenant's System in the closure.
            foreach (var (file, closure) in new[]
                     {
                         ("ck-system.ui-2.12.0.json", "System-2.8.0"), ("ck-system.notification-2.7.0.json", "System-2.8.0"),
                         ("ck-system.files-1.1.0.json", "System-2.5.0")
                     })
            {
                var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "testData",
                    "publishedSystemModels", file));
                var published = await DeserializeAsync(json);
                Assert.Equal([new CkModelId(closure)], published.Dependencies);
                fixture.Catalog.PublishFile(published.ModelId, json);
                withdraw.Add(published.ModelId);
                await InstallAsync(source, WithClosureOf(published, system));
            }

            await InstallAsync(source, WithClosureOf(await fixture.Catalog.LoadAsync(Identity380), system));

            var names = (await ModelIdsAsync(source)).Select(i => new CkModelId(i).Name).ToList();
            Assert.Equal(["System", "System.Files", IdentityName, "System.Notification", "System.StreamData", "System.UI"],
                names.Order(StringComparer.Ordinal));
            await test(source, names);
        }
        finally
        {
            foreach (var id in withdraw)
            {
                fixture.Catalog.Withdraw(id);
            }
        }
    }

    private static CkCompiledModelRoot WithClosureOf(CkCompiledModelRoot model, CkModelId system)
    {
        Assert.NotEqual([system], model.Dependencies);
        model.Dependencies = [system];
        return model;
    }

    private async Task ManipulateClosureAsync(string tenantId, string manipulation)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Database(tenantId);
        var models = database.GetCollection<BsonDocument>("CkModel");
        var ui = Builders<BsonDocument>.Filter.Eq("_id", "System.UI-2.12.0");
        var row = await models.Find(ui).FirstAsync(ct);
        Assert.Equal((await SystemIdAsync(tenantId)).FullName, Assert.Single(row["dependencies"].AsBsonArray).AsString);
        UpdateDefinition<BsonDocument> update;
        switch (manipulation)
        {
            case "closure entry of an unpublished version inside the range":
                update = Builders<BsonDocument>.Update.Set("dependencies", new BsonArray { "System-2.99.0" });
                break;
            case "closure entry of another major":
                update = Builders<BsonDocument>.Update.Set("dependencies", new BsonArray { "System-3.0.0" });
                break;
            case "closure entry added":
                update = Builders<BsonDocument>.Update.Push("dependencies", "System.Identity-3.8.0");
                break;
            case "declared floor lowered":
                Assert.Equal("2.5.0", row["dependencyRanges"].AsBsonArray.Single()["floor"].AsString);
                update = Builders<BsonDocument>.Update.Set("dependencyRanges.0.floor", "2.0.0");
                break;
            case "writeAccess removed next to a moved closure":
                var types = await database.GetCollection<BsonDocument>("CkType").UpdateManyAsync(
                    Builders<BsonDocument>.Filter.Eq("ckModelId", Identity380.FullName),
                    Builders<BsonDocument>.Update.Unset("writeAccess"), cancellationToken: ct);
                Assert.True(types.ModifiedCount > 0, "the dump must hold restricted identity types");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(manipulation), manipulation, null);
        }

        Assert.Equal(1, (await models.UpdateOneAsync(ui, update, cancellationToken: ct)).ModifiedCount);
    }

    private async Task<string> JsonOfAsync(CkCompiledModelRoot model)
    {
        using var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await fixture.GetService<ICkJsonSerializer>().SerializeAsync(writer, model);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<CkCompiledModelRoot> DeserializeAsync(string json)
    {
        var result = new OperationResult();
        var model = await fixture.GetService<ICkJsonSerializer>().DeserializeCompiledModelRootAsync(json, "test", result);
        Assert.False(result.HasErrors, string.Join("; ", result.Messages));
        return model;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private async Task AssertRefusedAndNothingLeftAsync(CommandResult result, string target, string reason)
    {
        Assert.False(result.Success);
        Assert.StartsWith(RestoreSystemModelGuard.RefusalCode, result.Error, StringComparison.Ordinal);
        Assert.Contains($"({reason})", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("writeAccess", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(await TenantExistsAsync(target), "a refused restore must not attach the tenant");
        Assert.False(await Sys.IsDatabaseExistingAsync(target), "a refused restore must not leave its database");
        Assert.False(await UserExistsAsync(target), "a refused restore must not leave a database user");
        // The scratch database of exactly this restore (other test collections restore on the same container).
        var scratch = ScratchDatabaseOf(target);
        Assert.DoesNotContain(scratch, await ListDatabasesAsync());
    }

    private async Task AssertGenericUserWriteRefusedAsync(ITenantContext tenant)
    {
        var repository = tenant.GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        var caller = RtSecurityContext.ForUser("caller-1", ["TenantManagement"], RtPrincipalKind.Interactive);
        var refused = await Assert.ThrowsAsync<RuntimeRepositoryException>(async () =>
        {
            using var session = await repository.GetSessionAsync(caller);
            session.StartTransaction();
            var entity = new RtEntity(UserTypeId, rtId, new Dictionary<string, object?>());
            await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(UserTypeId, entity)],
                new OperationResult());
            await session.CommitTransactionAsync();
        });
        Assert.Contains(refused.OperationResult.Messages, m => m.MessageNumber == 144);
    }

    /// <summary>The scratch database the pre-check of the restore of <paramref name="tenantId" /> used, from its log line.</summary>
    private string ScratchDatabaseOf(string tenantId)
    {
        var line = Assert.Single(fixture.Logs.Find(Microsoft.Extensions.Logging.LogLevel.Information,
            $"Restore of tenant '{tenantId}': checking the service-managed CK models of the dump in scratch database"));
        var name = line[(line.LastIndexOf(" '", StringComparison.Ordinal) + 2)..].TrimEnd('\'');
        Assert.Contains("restorecheck_", name, StringComparison.Ordinal);
        return name;
    }

    private string NewTenantId(string prefix)
    {
        var id = $"{prefix}{Guid.NewGuid():N}"[..20];
        _tenants.Add(id);
        return id;
    }

    private async Task<string> SourceTenantAsync(string prefix, CkCompiledModelRoot identity)
    {
        var tenantId = NewTenantId(prefix);
        await CreateTenantAsync(tenantId);
        await InstallAsync(tenantId, identity);
        return tenantId;
    }

    private async Task CreateTenantAsync(string tenantId)
    {
        using var session = await Sys.GetAdminSessionAsync();
        session.StartTransaction();
        await Sys.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task InstallAsync(string tenantId, CkCompiledModelRoot model) =>
        await CkModelImportGuardTests.InstallAsServiceAsync(await ResolveAsync(tenantId), model);

    private async Task<ITenantContext> ResolveAsync(string tenantId)
    {
        using var session = await Sys.GetAdminSessionAsync();
        session.StartTransaction();
        var tenant = await Sys.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return tenant;
    }

    private async Task<CkModelId> SystemIdAsync(string tenantId) =>
        new((await ModelIdsAsync(tenantId)).Single(id => id.StartsWith("System-", StringComparison.Ordinal)));

    private static CkCompiledModelRoot SmallModel(CkModelId modelId, CkModelId system) => new()
    {
        ModelId = modelId,
        Description = "restore check test model",
        Dependencies = [system],
        Attributes =
            [new CkAttributeDto { AttributeId = new CkAttributeId("RestoreCheck-1"), ValueType = AttributeValueTypesDto.String }]
    };

    private async Task ManipulateAsync(string tenantId, string manipulation)
    {
        var database = Database(tenantId);
        var ct = TestContext.Current.CancellationToken;
        var identityRows = Builders<BsonDocument>.Filter.Eq("ckModelId", Identity380.FullName);
        switch (manipulation)
        {
            case "writeAccess removed from the element rows":
                var update = await database.GetCollection<BsonDocument>("CkType").UpdateManyAsync(identityRows,
                    Builders<BsonDocument>.Update.Unset("writeAccess"), cancellationToken: ct);
                Assert.True(update.ModifiedCount > 0, "the dump must hold restricted identity types");
                break;
            case "model row changed":
                var model = await database.GetCollection<BsonDocument>("CkModel").UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", Identity380.FullName),
                    Builders<BsonDocument>.Update.Set("description", "self-built"), cancellationToken: ct);
                Assert.Equal(1, model.ModifiedCount);
                break;
            case "element row filed under another model":
                var types = database.GetCollection<BsonDocument>("CkType");
                var copy = (await types.Find(identityRows).FirstAsync(ct)).DeepClone().AsBsonDocument;
                var ownId = copy["_id"].AsString;
                var foreignId = ownId[..ownId.LastIndexOf('/')] + "/Backdoor-1";
                copy["_id"] = foreignId;
                if (copy.Contains("ckTypeId"))
                {
                    copy["ckTypeId"] = foreignId;
                }

                copy["ckModelId"] = (await SystemIdAsync(tenantId)).FullName;
                await types.InsertOneAsync(copy, cancellationToken: ct);
                break;
            case "model row of another id named System.Identity":
                // The by-name lookups of the import guard read ModelId: a row under another id that names
                // System.Identity would make every later embedded import of it skip as "newer installed".
                var modelRows = database.GetCollection<BsonDocument>("CkModel");
                var foreignModel = (await modelRows.Find(Builders<BsonDocument>.Filter.Eq("_id", Identity380.FullName))
                    .FirstAsync(ct)).DeepClone().AsBsonDocument;
                Assert.Equal(IdentityName, foreignModel["modelId"].AsString);
                foreignModel["_id"] = "Custom-3.99.0";
                await modelRows.InsertOneAsync(foreignModel, cancellationToken: ct);
                break;
            case "extra inheritance row":
                var inheritances = database.GetCollection<BsonDocument>("CkTypeInheritance");
                var extra = (await inheritances.Find(identityRows).FirstAsync(ct)).DeepClone().AsBsonDocument;
                extra["_id"] = ObjectId.GenerateNewId();
                await inheritances.InsertOneAsync(extra, cancellationToken: ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(manipulation), manipulation, null);
        }
    }

    private async Task<string> DumpAsync(string tenantId)
    {
        var archive = Path.Combine(Path.GetTempPath(), $"restorecheck-test-{Guid.NewGuid():N}.archive");
        _files.Add(archive);
        var dump = await fixture.GetService<IRepositoryOpsService>()
            .ExecuteMongoDumpAsync(MongoDumpOptions.ForArchive(tenantId.ToLowerInvariant(), archive));
        Assert.True(dump.Success, dump.Error);
        return archive;
    }

    private async Task<bool> TenantExistsAsync(string tenantId)
    {
        using var session = await Sys.GetAdminSessionAsync();
        session.StartTransaction();
        var exists = await Sys.IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return exists;
    }

    private async Task<bool> UserExistsAsync(string databaseName)
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var user = string.Format(config.DatabaseUser, databaseName.ToLowerInvariant());
        var result = await Database(config.AuthenticationDatabaseName).RunCommandAsync<BsonDocument>(
            new BsonDocument("usersInfo", user), cancellationToken: TestContext.Current.CancellationToken);
        return result["users"].AsBsonArray.Count > 0;
    }

    private async Task<List<string>> ListDatabasesAsync() =>
        await (await Client.ListDatabaseNamesAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

    private async Task DropDatabaseAsync(string databaseName) =>
        await Client.DropDatabaseAsync(databaseName.ToLowerInvariant(), TestContext.Current.CancellationToken);

    private async Task<List<string>> ModelIdsAsync(string tenantId) =>
        (await Database(tenantId).GetCollection<BsonDocument>("CkModel").Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken)).Select(d => d["_id"].AsString).ToList();

    private async Task<string> SnapshotOfPublishedAsync(CkModelId modelId) =>
        CkCompiledModelCanonicalizer.ToCanonicalJson(await fixture.Catalog.LoadAsync(modelId));

    private async Task<string> ReadBackCanonicalAsync(string tenantId, CkModelId modelId)
    {
        var context = (TenantContext)Sys;
        var models = await Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.DatabaseCkModelRepository.ReadServiceManagedModelsAsync(
            context.CreateAdminDataSourceForRestore(tenantId, tenantId));
        return CkCompiledModelCanonicalizer.ToCanonicalJson(models.Single(m => m.Row.Id == modelId).Compiled);
    }

    /// <summary>Every CK document of <paramref name="modelName" /> in the tenant database as comparable text.</summary>
    private async Task<string> SnapshotAsync(string tenantId, string modelName)
    {
        var database = Database(tenantId);
        var ct = TestContext.Current.CancellationToken;
        var names = (await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct))
            .Where(n => n.StartsWith("Ck", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var name in names)
        {
            var filter = Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("modelId", modelName),
                Builders<BsonDocument>.Filter.Regex("ckModelId",
                    new BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(modelName)}-[0-9]")));
            var documents = await database.GetCollection<BsonDocument>(name).Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(ct);
            parts.Add($"{name}:{string.Join("|", documents.Select(d => d.ToJson()))}");
        }

        return string.Join("\n", parts);
    }

    private IMongoDatabase Database(string name) => Client.GetDatabase(name.ToLowerInvariant());

    private MongoClient Client => _client ??= CreateClient();

    private MongoClient CreateClient()
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
        return new MongoClient(urlBuilder.ToMongoUrl());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tenant in _tenants)
        {
            await ThrowawayTenant.DropAsync(Sys, tenant);
            if (await Sys.IsDatabaseExistingAsync(tenant))
            {
                await DropDatabaseAsync(tenant);
            }
        }

        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }

        _client?.Dispose();
    }
}
