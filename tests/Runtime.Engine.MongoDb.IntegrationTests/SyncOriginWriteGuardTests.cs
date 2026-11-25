using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.Repositories;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

using DeleteOptions = Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The write rule of synced master data: an entity that carries <c>RtSyncOrigin</c> is owned by the tenant it names.
///     Every write of the repository funnel except the sync writer of that tenant is refused when it changes, replaces,
///     deletes or archives such an entity, or when it sets or changes <c>RtSyncOrigin</c>; the sync writer touches only
///     the copies of its own source tenant. Entities without an origin are unaffected. The engine maintenance channel
///     (<see cref="MongoRuntimeRepositoryProvider" />) is exempt.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class SyncOriginWriteGuardTests(ImportTestCkModelFixture fixture)
{
    private const string GroupTenant = "group-a";
    private const string OtherGroupTenant = "group-b";
    private static readonly RtCkId<CkTypeId> EntityType = new("Test/SeedGuardEntity");

    private static readonly RtSecurityContext Caller =
        RtSecurityContext.ForUser("caller-1", ["TenantManagement"], RtPrincipalKind.Interactive);

    private async Task<ITenantRepository> CleanRepositoryAsync()
    {
        await fixture.ClearCollectionAsync();
        return fixture.GetSystemContext().GetTenantRepository();
    }

    private static RtSyncOriginRecord Origin(string sourceTenantId, OctoObjectId sourceRtId) =>
        new() { SourceTenantId = sourceTenantId, SourceRtId = sourceRtId.ToString(), SyncedAt = DateTime.UtcNow };

    private static RtEntity Entity(OctoObjectId rtId, string text, RtSyncOriginRecord? origin = null)
    {
        var attributes = new Dictionary<string, object?> { { "GuardText", text } };
        if (origin != null)
        {
            attributes[SyncOriginWriteRules.AttributeName] = origin;
        }

        return new RtEntity(EntityType, rtId, attributes);
    }

    private static async Task WriteAsync(IOctoSession session, ITenantRepository repository,
        params IEntityUpdateInfo<RtEntity>[] updates)
    {
        session.StartTransaction();
        await repository.ApplyChangesAsync(session, updates, new OperationResult());
        await session.CommitTransactionAsync();
    }

    /// <summary>Stores a copy of <paramref name="sourceTenantId" /> the way the sync does.</summary>
    private static async Task<OctoObjectId> InsertCopyAsync(ITenantRepository repository, string sourceTenantId,
        string text = "copy")
    {
        var rtId = OctoObjectId.GenerateNewId();
        using var session = await repository.GetSyncWriterSessionAsync(Caller, sourceTenantId);
        await WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateInsert(Entity(rtId, text, Origin(sourceTenantId, rtId))));
        return rtId;
    }

    private static async Task<OctoObjectId> InsertLocalAsync(ITenantRepository repository, string text = "local")
    {
        var rtId = OctoObjectId.GenerateNewId();
        using var session = await repository.GetSessionAsync(Caller);
        await WriteAsync(session, repository, EntityUpdateInfo<RtEntity>.CreateInsert(Entity(rtId, text)));
        return rtId;
    }

    private static async Task<RtEntity?> ReadAsync(ITenantRepository repository, OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        var result = await repository.GetRtEntitiesByIdAsync(session, EntityType, [rtId],
            RtEntityQueryOptions.Create());
        return result.Items.SingleOrDefault();
    }

    private static void AssertRefused(RuntimeRepositoryException ex, OctoObjectId rtId)
    {
        var message = Assert.Single(ex.OperationResult.Messages,
            m => m.MessageNumber == SyncOriginWriteRules.MessageNumber);
        Assert.Equal(SyncOriginWriteRules.ErrorCode, message.Parameters["code"]);
        Assert.Equal(rtId.ToString(), message.Parameters["rtId"]);
        Assert.True(SyncOriginWriteRules.IsRefusal(ex));
    }

    public static TheoryData<string> PlainSessions => new() { "system", "caller" };

    private static Task<IOctoSession> PlainSessionAsync(ITenantRepository repository, string kind) =>
        kind == "system" ? repository.GetSessionAsync() : repository.GetSessionAsync(Caller);

    [Theory]
    [MemberData(nameof(PlainSessions))]
    public async Task PlainSession_UpdateOfACopy_IsRefused_AndTheCopyIsUnchanged(string kind)
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);

        using var session = await PlainSessionAsync(repository, kind);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, copy), Entity(copy, "edited"))));

        AssertRefused(ex, copy);
        Assert.Equal("copy", (await ReadAsync(repository, copy))!.GetAttributeStringValueOrDefault("GuardText"));
    }

    [Theory]
    [MemberData(nameof(PlainSessions))]
    public async Task PlainSession_ReplaceOfACopy_IsRefused(string kind)
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);

        using var session = await PlainSessionAsync(repository, kind);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateReplace(new RtEntityId(EntityType, copy), Entity(copy, "replaced"))));

        AssertRefused(ex, copy);
    }

    [Theory]
    [InlineData(DeleteStrategies.Erase)]
    [InlineData(DeleteStrategies.Archive)]
    public async Task CallerSession_DeleteOrArchiveOfACopy_IsRefused_AndTheCopyStays(DeleteStrategies strategy)
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);
        var before = (await ReadAsync(repository, copy))!;
        // A fresh entity carries no state (null reads as active); an archive would set Archived and a date.
        Assert.NotEqual(RtState.Archived, before.RtState);

        using (var session = await repository.GetSessionAsync(Caller))
        {
            session.StartTransaction();
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                repository.DeleteOneRtEntityByRtIdAsync(session, EntityType, copy,
                    new DeleteOptions { Strategy = strategy }));
            AssertRefused(ex, copy);
        }

        var after = await ReadAsync(repository, copy);
        Assert.NotNull(after);
        Assert.Equal(before.RtState, after.RtState);
        Assert.Equal(before.RtArchivedDateTime, after.RtArchivedDateTime);
        Assert.Equal(before.RtVersion, after.RtVersion);
    }

    [Fact]
    public async Task CallerSession_FilterUpdateAndFilterDelete_SkipACopySilently_AndWriteTheLocalEntity()
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant, "shared");
        var local = await InsertLocalAsync(repository, "shared");
        var filter = FieldFilterCriteria.Create().FieldEquals("GuardText", "shared");

        using (var session = await repository.GetSessionAsync(Caller))
        {
            session.StartTransaction();
            var update = await repository.CreateTransientRtEntityByRtCkIdAsync(EntityType);
            update.SetAttributeValue("GuardCount", AttributeValueTypesDto.Int, 7);
            await repository.UpdateManyRtEntityAsync(session, filter, update);
            await session.CommitTransactionAsync();
        }

        Assert.Equal(7, (await ReadAsync(repository, local))!.GetAttributeValueOrDefault<int>("GuardCount"));
        Assert.Null((await ReadAsync(repository, copy))!.GetAttributeValueOrDefault("GuardCount"));

        using (var session = await repository.GetSessionAsync(Caller))
        {
            session.StartTransaction();
            await repository.DeleteManyRtEntitiesAsync(session, EntityType, filter, DeleteOptions.Erase);
            await session.CommitTransactionAsync();
        }

        Assert.Null(await ReadAsync(repository, local));
        Assert.NotNull(await ReadAsync(repository, copy));
    }

    [Fact]
    public async Task CallerSession_FilterDeleteOfOneEntity_DoesNotFindACopy()
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant, "only-copy");

        using var session = await repository.GetSessionAsync(Caller);
        session.StartTransaction();
        await Assert.ThrowsAsync<TenantRepositoryException>(() => repository.DeleteOneRtEntityAsync(session,
            EntityType, FieldFilterCriteria.Create().FieldEquals("GuardText", "only-copy"), DeleteOptions.Erase));

        Assert.NotNull(await ReadAsync(repository, copy));
    }

    [Fact]
    public async Task TheWriteScope_EndsWithTheFunnelCall_AndConcurrentSessionsDoNotShareIt()
    {
        var repository = await CleanRepositoryAsync();
        var locals = new List<OctoObjectId>();
        for (var i = 0; i < 10; i++)
        {
            locals.Add(await InsertLocalAsync(repository, $"local-{i}"));
        }

        Assert.Null(SyncOriginWriteScope.Current);

        // A plain session updates local entities while a sync-writer session inserts copies, interleaved. A scope
        // leaking from one to the other would refuse the plain update (sync filter) or the copy insert's later update.
        var plain = Task.Run(async () =>
        {
            foreach (var local in locals)
            {
                using var session = await repository.GetSessionAsync(Caller);
                await WriteAsync(session, repository, EntityUpdateInfo<RtEntity>.CreateUpdate(
                    new RtEntityId(EntityType, local), Entity(local, "edited")));
                Assert.Null(SyncOriginWriteScope.Current);
            }
        }, TestContext.Current.CancellationToken);
        var sync = Task.Run(async () =>
        {
            var copies = new List<OctoObjectId>();
            for (var i = 0; i < locals.Count; i++)
            {
                var copy = await InsertCopyAsync(repository, GroupTenant, $"copy-{i}");
                using var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant);
                await WriteAsync(session, repository, EntityUpdateInfo<RtEntity>.CreateUpdate(
                    new RtEntityId(EntityType, copy), Entity(copy, "synced", Origin(GroupTenant, copy))));
                Assert.Null(SyncOriginWriteScope.Current);
                copies.Add(copy);
            }

            return copies;
        }, TestContext.Current.CancellationToken);

        await plain;
        var syncedCopies = await sync;

        foreach (var local in locals)
        {
            Assert.Equal("edited",
                (await ReadAsync(repository, local))!.GetAttributeStringValueOrDefault("GuardText"));
        }

        foreach (var copy in syncedCopies)
        {
            Assert.Equal("synced",
                (await ReadAsync(repository, copy))!.GetAttributeStringValueOrDefault("GuardText"));
        }

        // After the funnel returned, a write without the funnel runs without a scope.
        Assert.Null(SyncOriginWriteScope.Current);
    }

    [Theory]
    [MemberData(nameof(PlainSessions))]
    public async Task PlainSession_InsertWithAnOrigin_IsRefused_AndNothingIsStored(string kind)
    {
        var repository = await CleanRepositoryAsync();
        var rtId = OctoObjectId.GenerateNewId();

        using var session = await PlainSessionAsync(repository, kind);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateInsert(Entity(rtId, "forged", Origin(GroupTenant, rtId)))));

        AssertRefused(ex, rtId);
        Assert.Null(await ReadAsync(repository, rtId));
    }

    [Fact]
    public async Task CallerSession_UpdateThatSetsAnOriginOnALocalEntity_IsRefused()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);

        using var session = await repository.GetSessionAsync(Caller);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local),
                Entity(local, "pinned", Origin(GroupTenant, local)))));

        AssertRefused(ex, local);
        var stored = await ReadAsync(repository, local);
        Assert.Null(stored!.GetAttributeValueOrDefault(SyncOriginWriteRules.AttributeName));
    }

    [Fact]
    public async Task CallerSession_EntityWithoutAnOrigin_IsWrittenAsBefore()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);

        using (var session = await repository.GetSessionAsync(Caller))
        {
            await WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local), Entity(local, "edited")));
        }

        Assert.Equal("edited",
            (await ReadAsync(repository, local))!.GetAttributeStringValueOrDefault("GuardText"));

        using (var session = await repository.GetSessionAsync(Caller))
        {
            await WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(EntityType, local)));
        }

        Assert.Null(await ReadAsync(repository, local));
    }

    [Fact]
    public async Task CallerSession_MixedBatch_IsRefusedAsAWhole_AndTheLocalEntityIsUnchanged()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);
        var copy = await InsertCopyAsync(repository, GroupTenant);

        using var session = await repository.GetSessionAsync(Caller);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local), Entity(local, "edited")),
            EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(EntityType, copy))));

        AssertRefused(ex, copy);
        Assert.Equal("local",
            (await ReadAsync(repository, local))!.GetAttributeStringValueOrDefault("GuardText"));
        Assert.NotNull(await ReadAsync(repository, copy));
    }

    [Fact]
    public async Task SyncWriter_UpdatesAndDeletesTheCopiesOfItsOwnSource()
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            await WriteAsync(session, repository, EntityUpdateInfo<RtEntity>.CreateUpdate(
                new RtEntityId(EntityType, copy), Entity(copy, "synced again", Origin(GroupTenant, copy))));
        }

        Assert.Equal("synced again",
            (await ReadAsync(repository, copy))!.GetAttributeStringValueOrDefault("GuardText"));

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            await WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(EntityType, copy)));
        }

        Assert.Null(await ReadAsync(repository, copy));
    }

    [Fact]
    public async Task SyncWriter_CopyOfAnotherSource_IsRefused()
    {
        var repository = await CleanRepositoryAsync();
        var foreign = await InsertCopyAsync(repository, OtherGroupTenant);

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, foreign),
                    Entity(foreign, "taken over", Origin(GroupTenant, foreign)))));
            AssertRefused(ex, foreign);
        }

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(EntityType, foreign))));
            AssertRefused(ex, foreign);
        }

        Assert.NotNull(await ReadAsync(repository, foreign));
    }

    [Fact]
    public async Task SyncWriter_NeverTouchesAnEntityWithoutAnOrigin()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);

        using var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local),
                Entity(local, "claimed", Origin(GroupTenant, local)))));

        AssertRefused(ex, local);
        Assert.Equal("local",
            (await ReadAsync(repository, local))!.GetAttributeStringValueOrDefault("GuardText"));
    }

    [Fact]
    public async Task SyncWriter_InsertWithoutItsOwnOrigin_IsRefused()
    {
        var repository = await CleanRepositoryAsync();
        var withoutOrigin = OctoObjectId.GenerateNewId();
        var otherOrigin = OctoObjectId.GenerateNewId();

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateInsert(Entity(withoutOrigin, "plain"))));
            AssertRefused(ex, withoutOrigin);
        }

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateInsert(Entity(otherOrigin, "other",
                    Origin(OtherGroupTenant, otherOrigin)))));
            AssertRefused(ex, otherOrigin);
        }

        Assert.Null(await ReadAsync(repository, withoutOrigin));
        Assert.Null(await ReadAsync(repository, otherOrigin));
    }

    [Fact]
    public async Task SyncWriterSession_NeedsASourceTenant()
    {
        var repository = await CleanRepositoryAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetSyncWriterSessionAsync(Caller, " "));
    }

    [Fact]
    public async Task EveryRepositoryOfTheTenantContext_RefusesASystemSession_ToDeleteACopy()
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);
        var tenantContext = await fixture.GetSystemContext().TryFindTenantContextAsync(repository.TenantId);
        Assert.NotNull(tenantContext);

        foreach (var handedOut in new[] { tenantContext.GetTenantRepository(), tenantContext.GetTenantRepositoryAsAdmin() })
        {
            using var session = await handedOut.GetSessionAsync();
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, handedOut,
                EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(EntityType, copy))));
            AssertRefused(ex, copy);
        }

        Assert.NotNull(await ReadAsync(repository, copy));
    }

    [Fact]
    public async Task MaintenanceRepository_OfTheEngineServices_StillChangesAndDeletesCopies()
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);
        var provider = new MongoRuntimeRepositoryProvider(fixture.GetSystemContext(),
            NullLogger<MongoRuntimeRepositoryProvider>.Instance, []);
        var maintenance = await provider.GetRepositoryAsync(repository.TenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(maintenance);

        using (var session = await maintenance.GetSessionAsync())
        {
            session.StartTransaction();
            await maintenance.UpdateOneRtEntityByIdAsync(session, EntityType, copy, Entity(copy, "migrated"));
            await session.CommitTransactionAsync();
        }

        Assert.Equal("migrated",
            (await ReadAsync(repository, copy))!.GetAttributeStringValueOrDefault("GuardText"));

        using (var session = await maintenance.GetSessionAsync())
        {
            session.StartTransaction();
            await maintenance.DeleteOneRtEntityByRtIdAsync(session, EntityType, copy, DeleteOptions.Erase);
            await session.CommitTransactionAsync();
        }

        Assert.Null(await ReadAsync(repository, copy));
    }

    // ---- changes judged before the write: bulk-mode insert, linked binaries --------------------------------------

    private async Task ApplyGuardedAsync(IOctoSession session, ITenantRepository repository,
        BulkRtMutationOptions options, params IEntityUpdateInfo<RtEntity>[] updates)
    {
        // The repository API has no bulk-mode option; the guarded funnel is called the way TenantContext wires it.
        var guarded = new SyncOriginGuardedBulkRtMutation(fixture.GetService<IBulkRtMutation>());
        session.StartTransaction();
        await guarded.ApplyChangesAsync(session, ((TenantRepository)repository).DataSource,
            fixture.GetService<ICkCacheService>(), updates, [], options, new OperationResult());
        await session.CommitTransactionAsync();
    }

    [Theory]
    [InlineData(BulkInsertStrategies.Upsert)]
    [InlineData(BulkInsertStrategies.InsertOnly)]
    public async Task PlainSession_BulkModeInsertOverACopy_IsRefused_AndTheCopyStays(BulkInsertStrategies strategy)
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);

        using var session = await repository.GetSessionAsync(Caller);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => ApplyGuardedAsync(session, repository,
            new BulkRtMutationOptions { UseBulkMode = true, BulkInsertStrategy = strategy },
            EntityUpdateInfo<RtEntity>.CreateInsert(Entity(copy, "overwritten"))));

        AssertRefused(ex, copy);
        var stored = (await ReadAsync(repository, copy))!;
        Assert.Equal("copy", stored.GetAttributeStringValueOrDefault("GuardText"));
        Assert.NotNull(stored.GetAttributeValueOrDefault(SyncOriginWriteRules.AttributeName));
    }

    [Fact]
    public async Task SyncWriter_BulkModeInsertOverAnotherSourcesCopy_IsRefused()
    {
        var repository = await CleanRepositoryAsync();
        var foreign = await InsertCopyAsync(repository, OtherGroupTenant);

        using var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => ApplyGuardedAsync(session, repository,
            new BulkRtMutationOptions { UseBulkMode = true, BulkInsertStrategy = BulkInsertStrategies.Upsert },
            EntityUpdateInfo<RtEntity>.CreateInsert(Entity(foreign, "taken over", Origin(GroupTenant, foreign)))));

        AssertRefused(ex, foreign);
        Assert.Equal("copy", (await ReadAsync(repository, foreign))!.GetAttributeStringValueOrDefault("GuardText"));
    }

    [Fact]
    public async Task BulkModeInsert_OverAnEntityTheSessionOwns_StillUpserts()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);
        var copy = await InsertCopyAsync(repository, GroupTenant);
        var options = new BulkRtMutationOptions { UseBulkMode = true, BulkInsertStrategy = BulkInsertStrategies.Upsert };

        using (var session = await repository.GetSessionAsync(Caller))
        {
            await ApplyGuardedAsync(session, repository, options,
                EntityUpdateInfo<RtEntity>.CreateInsert(Entity(local, "upserted")));
        }

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            await ApplyGuardedAsync(session, repository, options,
                EntityUpdateInfo<RtEntity>.CreateInsert(Entity(copy, "synced", Origin(GroupTenant, copy))));
        }

        Assert.Equal("upserted", (await ReadAsync(repository, local))!.GetAttributeStringValueOrDefault("GuardText"));
        Assert.Equal("synced", (await ReadAsync(repository, copy))!.GetAttributeStringValueOrDefault("GuardText"));
    }

    private static readonly RtCkId<CkTypeId> BinaryType = new("Test/BinaryEntity");

    private static EntityBinaryInfo Binary(string fileName) => new()
    {
        Filename = fileName,
        ContentType = "application/octet-stream",
        Stream = File.OpenRead("testData/largeBinaries/" + fileName)
    };

    /// <summary>A copy of a <c>BinaryEntity</c> with a linked binary, written by the sync of <paramref name="sourceTenantId" />.</summary>
    private static async Task<(OctoObjectId RtId, OctoObjectId BinaryId)> InsertBinaryCopyAsync(
        ITenantRepository repository, string sourceTenantId)
    {
        var rtId = OctoObjectId.GenerateNewId();
        var entity = new RtEntity(BinaryType, rtId, new Dictionary<string, object?>
        {
            { "Binary", Binary("Customers.xlsx") },
            { "DataCount", 5 },
            { SyncOriginWriteRules.AttributeName, Origin(sourceTenantId, rtId) }
        });
        using (var session = await repository.GetSyncWriterSessionAsync(Caller, sourceTenantId))
        {
            await WriteAsync(session, repository, EntityUpdateInfo<RtEntity>.CreateInsert(entity));
        }

        using var readSession = await repository.GetSessionAsync();
        var stored = await repository.GetRtEntityByRtIdAsync<RtBinaryEntity>(readSession, rtId);
        return (rtId, stored!.Binary!.BinaryId!.Value);
    }

    private static async Task<long?> BinarySizeAsync(ITenantRepository repository, OctoObjectId binaryId)
    {
        using var session = await repository.GetSessionAsync();
        try
        {
            using var handler = await repository.DownloadLargeBinaryAsync(session, binaryId);
            using var bytes = new MemoryStream();
            await handler.Stream.CopyToAsync(bytes);
            return bytes.Length;
        }
        catch (EntityNotFoundException)
        {
            return null;
        }
    }

    [Theory]
    [InlineData(EntityModOptions.Replace, false)]
    [InlineData(EntityModOptions.Update, false)]
    [InlineData(EntityModOptions.Update, true)]
    public async Task PlainSession_WithoutATransaction_ARefusedBinaryWriteOfACopy_LeavesItsBytes(
        EntityModOptions modOption, bool referencesTheStoredBinary)
    {
        var repository = await CleanRepositoryAsync();
        var (copy, binaryId) = await InsertBinaryCopyAsync(repository, GroupTenant);
        var sizeBefore = await BinarySizeAsync(repository, binaryId);
        Assert.NotNull(sizeBefore);

        var binary = Binary("Products.pdf");
        if (referencesTheStoredBinary)
        {
            binary.BinaryId = binaryId;
        }

        var change = new RtEntity(BinaryType, copy,
            new Dictionary<string, object?> { { "Binary", binary }, { "DataCount", 7 } });
        var entityId = new RtEntityId(BinaryType, copy);
        var update = modOption == EntityModOptions.Replace
            ? EntityUpdateInfo<RtEntity>.CreateReplace(entityId, change)
            : EntityUpdateInfo<RtEntity>.CreateUpdate(entityId, change);

        // No transaction: a linked binary is deleted or replaced at once, so the refusal has to come before it.
        using (var session = await repository.GetSessionAsync(Caller))
        {
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                repository.ApplyChangesAsync(session, new IEntityUpdateInfo<RtEntity>[] { update },
                    new OperationResult()));
            AssertRefused(ex, copy);
        }

        Assert.Equal(sizeBefore, await BinarySizeAsync(repository, binaryId));
        using var readSession = await repository.GetSessionAsync();
        var stored = await repository.GetRtEntityByRtIdAsync<RtBinaryEntity>(readSession, copy);
        Assert.Equal(binaryId, stored!.Binary!.BinaryId);
    }

    // ---- legacy `attributes: null` and member paths of the origin ------------------------------------------------

    [Fact]
    public async Task SyncWriter_UpdateOfALocalEntityWithStoredNullAttributes_IsRefused_AndNotRepaired()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);
        var collection = await RawCollectionAsync(local);
        var id = new BsonDocument("_id", new ObjectId(local.ToString()));
        await collection.UpdateOneAsync(id, new BsonDocument("$set", new BsonDocument("attributes", BsonNull.Value)),
            cancellationToken: TestContext.Current.CancellationToken);

        using (var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant))
        {
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local),
                    Entity(local, "claimed", Origin(GroupTenant, local)))));
            AssertRefused(ex, local);
        }

        var raw = await collection.Find(id).FirstAsync(TestContext.Current.CancellationToken);
        Assert.True(raw["attributes"].IsBsonNull);
    }

    [Fact]
    public async Task PlainSession_UpdateOfALocalEntityWithStoredNullAttributes_IsStillRepaired()
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);
        var collection = await RawCollectionAsync(local);
        var id = new BsonDocument("_id", new ObjectId(local.ToString()));
        await collection.UpdateOneAsync(id, new BsonDocument("$set", new BsonDocument("attributes", BsonNull.Value)),
            cancellationToken: TestContext.Current.CancellationToken);

        using (var session = await repository.GetSessionAsync(Caller))
        {
            await WriteAsync(session, repository,
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local), Entity(local, "healed")));
        }

        Assert.Equal("healed", (await ReadAsync(repository, local))!.GetAttributeStringValueOrDefault("GuardText"));
    }

    [Theory]
    [InlineData("RtSyncOrigin.SourceTenantId")]
    [InlineData("rtSyncOrigin.attributes.sourceTenantId")]
    [InlineData("RTSYNCORIGIN.SourceRtId")]
    public async Task CallerSession_UpdateOfAnOriginMemberPath_IsRefused(string key)
    {
        var repository = await CleanRepositoryAsync();
        var local = await InsertLocalAsync(repository);

        using var session = await repository.GetSessionAsync(Caller);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, local),
                new RtEntity(EntityType, local, new Dictionary<string, object?> { { key, GroupTenant } }))));

        AssertRefused(ex, local);
        var raw = await (await RawCollectionAsync(local))
            .Find(new BsonDocument("_id", new ObjectId(local.ToString())))
            .FirstAsync(TestContext.Current.CancellationToken);
        Assert.False(raw["attributes"].AsBsonDocument.Contains("rtSyncOrigin"));
    }

    [Fact]
    public async Task SyncWriter_UpdateOfAnOriginMemberPathOfItsOwnCopy_IsRefused()
    {
        var repository = await CleanRepositoryAsync();
        var copy = await InsertCopyAsync(repository, GroupTenant);

        using var session = await repository.GetSyncWriterSessionAsync(Caller, GroupTenant);
        var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => WriteAsync(session, repository,
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(EntityType, copy),
                new RtEntity(EntityType, copy,
                    new Dictionary<string, object?> { { "rtsyncorigin.sourceTenantId", OtherGroupTenant } }))));

        AssertRefused(ex, copy);
    }

    private async Task<IMongoCollection<BsonDocument>> RawCollectionAsync(OctoObjectId rtId)
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
        var db = new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(config.SystemDatabaseName);
        var id = new BsonDocument("_id", new ObjectId(rtId.ToString()));
        using var cursor = await db.ListCollectionsAsync(new ListCollectionsOptions
        {
            Filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity_"))
        });
        foreach (var info in await cursor.ToListAsync())
        {
            var collection = db.GetCollection<BsonDocument>(info["name"].AsString);
            if (await collection.CountDocumentsAsync(id) > 0)
            {
                return collection;
            }
        }

        throw new InvalidOperationException($"No collection holds entity {rtId}.");
    }
}
