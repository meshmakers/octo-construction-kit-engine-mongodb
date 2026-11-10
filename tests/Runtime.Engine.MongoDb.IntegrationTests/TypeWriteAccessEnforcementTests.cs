using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8574 end to end against MongoDB: a model with <c>writeAccess</c> blocks is imported into a throwaway tenant, the
///     block survives into the tenant's CK cache, and the real repository write path admits or refuses per principal kind
///     and channel. A refused write leaves nothing in the database; a system session and an unrestricted type keep writing.
///     Model: the C# kitchen sink, whose abstract <c>Thing</c> declares principals [Interactive] and channels
///     [KitchenSink.Tools/Writer] (so <c>Gadget</c> inherits it); the test adds principals [Interactive] on <c>Widget</c>.
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class TypeWriteAccessEnforcementTests(CkModelImportMigrationFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Widget = new("KitchenSinkCs/Widget");
    private static readonly RtCkId<CkTypeId> Gadget = new("KitchenSinkCs/Gadget");

    private static RtSecurityContext Caller(RtPrincipalKind kind, string? channel = null)
    {
        var context = RtSecurityContext.ForUser("caller-1", ["TenantManagement"], kind);
        return channel == null ? context : context.WithWriteChannel(channel);
    }

    private async Task WithRestrictedTenantAsync(Func<ITenantRepository, Task> body)
    {
        var helper = new CkMetaModelRoundTripTests(fixture);
        await helper.WithThrowawayTenantAsync("rtwrite", async (tenant, _) =>
        {
            var compiled = CkV2KitchenSinkModel.Build(await helper.GetInstalledSystemIdAsync(tenant));
            compiled.Types!.Single(t => t.TypeId.Name == "Widget").WriteAccess =
                new CkTypeWriteAccessDto { Principals = [CkWritePrincipals.Interactive] };
            await tenant.ImportCkModelAsync(compiled);
            await body(tenant.GetTenantRepository());
        });
    }

    private static async Task InsertWidgetAsync(ITenantRepository repository, RtSecurityContext? caller,
        OctoObjectId rtId)
    {
        using var session = caller == null ? await repository.GetSessionAsync() : await repository.GetSessionAsync(caller);
        session.StartTransaction();
        var entity = new RtEntity(Widget, rtId, new Dictionary<string, object?> { { "Name", $"w-{rtId}" } });
        await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(Widget, entity)],
            new OperationResult());
        await session.CommitTransactionAsync();
    }

    private static async Task<bool> ExistsAsync(ITenantRepository repository, RtCkId<CkTypeId> ckTypeId,
        OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        var result = await repository.GetRtEntitiesByIdAsync(session, ckTypeId, [rtId], RtEntityQueryOptions.Create());
        return result.Items.Any();
    }

    [Fact]
    public async Task ClientCaller_IsRefusedWith144_AndNothingIsPersisted_InteractiveAndSystemWrite()
    {
        await WithRestrictedTenantAsync(async repository =>
        {
            var refused = OctoObjectId.GenerateNewId();
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                InsertWidgetAsync(repository, Caller(RtPrincipalKind.Client), refused));
            var message = Assert.Single(ex.OperationResult.Messages, m => m.MessageNumber == 144);
            Assert.Equal("KitchenSinkCs/Widget", message.Parameters["ckTypeId"]);
            Assert.False(await ExistsAsync(repository, Widget, refused));

            var unknown = OctoObjectId.GenerateNewId();
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                InsertWidgetAsync(repository, Caller(RtPrincipalKind.Unknown), unknown));
            Assert.False(await ExistsAsync(repository, Widget, unknown));

            var interactive = OctoObjectId.GenerateNewId();
            await InsertWidgetAsync(repository, Caller(RtPrincipalKind.Interactive), interactive);
            Assert.True(await ExistsAsync(repository, Widget, interactive));

            var system = OctoObjectId.GenerateNewId();
            await InsertWidgetAsync(repository, null, system);
            Assert.True(await ExistsAsync(repository, Widget, system));
        });
    }

    [Fact]
    public async Task InheritedRestriction_RefusesAClientOnTheDerivedType_AndNamesTheBaseType()
    {
        await WithRestrictedTenantAsync(async repository =>
        {
            // Gadget declares nothing; the block comes from its abstract base Thing (via the tenant's CK cache).
            using var session = await repository.GetSessionAsync(Caller(RtPrincipalKind.Client));
            session.StartTransaction();
            var gadget = new RtEntity(Gadget, OctoObjectId.GenerateNewId(),
                new Dictionary<string, object?> { { "Name", "g" } });
            var ex = await Assert.ThrowsAsync<RuntimeRepositoryException>(() => repository.ApplyChangesAsync(session,
                [EntityUpdateInfo<RtEntity>.CreateInsert(Gadget, gadget)], new OperationResult()));
            var message = Assert.Single(ex.OperationResult.Messages, m => m.MessageNumber == 144);
            Assert.Equal("KitchenSinkCs/Thing", message.Parameters["restrictingCkTypeId"]);
            Assert.Equal("KitchenSink.Tools/Writer", message.Parameters["channels"]);
            Assert.False(await ExistsAsync(repository, Gadget, gadget.RtId));

            // Widget's own block lists no channel, so the channel of Thing does not help there.
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() => InsertWidgetAsync(repository,
                Caller(RtPrincipalKind.Client, "KitchenSink.Tools/Writer"), OctoObjectId.GenerateNewId()));
        });
    }
}
