using System.Text;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;

/// <summary>
///     CK v2 F1.0 against a real MongoDB, in throwaway child tenants:
///     <list type="bullet">
///         <item>
///             AB#5900 downgrade guard: a service's embedded model never replaces a newer installed version (same major
///             → INFO, newer major → WARN), an older installed version is upgraded, the same version short-circuits;
///             a skip sends no tenant-update notification; explicit imports may downgrade with a WARN.
///         </item>
///         <item>
///             AB#5901 re-validation: a <c>ResolveFailed</c> model returns to <c>Available</c> after the import that
///             makes its pin satisfiable again (the R2-3 Basic.Accounting scenario), and stays <c>ResolveFailed</c>
///             without flapping while the pin is still unmet.
///         </item>
///     </list>
///     Newer/older System versions are the embedded System compiled model with its id rewritten (same content).
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class CkModelImportGuardTests(CkModelImportGuardFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static readonly CkModelId TestV2ModelId = new("Test-2.0.0");
    private static CkModelId EmbeddedSystem => SystemCkIds.CkModelId;

    [Fact]
    public async Task EmbeddedSystem_OlderThanInstalledSameMajor_DoesNotReplaceIt_AndSendsNoNotification()
    {
        await WithTenantAsync("guardsame", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var newer = Bump(EmbeddedSystem, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, newer));
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));

            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await ResolveAsync(tenantId); // runs UpdateSystemCkModelAsync with the embedded (older) System

            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "downgrade prevented", tenantId, newer.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.EmbeddedImportSkippedCounterName,
                ("model", "System"), ("reason", CkModelImportDiagnostics.ReasonNewerInstalled)) >= 1);
        });
    }

    [Fact]
    public async Task EmbeddedSystem_InstalledHasHigherMajor_SkipsWithWarning()
    {
        await WithTenantAsync("guardmajor", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var nextMajor = new CkModelId("System", new CkVersion(EmbeddedSystem.Version.Major + 1, 0, 0));
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, nextMajor));

            var pos = fixture.Notifications.PosUpdates;
            await ResolveAsync(tenantId);

            Assert.Equal(nextMajor.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal(pos, fixture.Notifications.PosUpdates);
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "too old for the tenant", tenantId,
                EmbeddedSystem.FullName, nextMajor.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.EmbeddedImportSkippedCounterName,
                ("model", "System"), ("reason", CkModelImportDiagnostics.ReasonNewerMajorInstalled)) >= 1);
        });
    }

    /// <summary>
    ///     Explicit import of an OLDER System is allowed (decision Q5) with a WARN and a counter. It does not stick
    ///     in a process whose embedded System is newer: the next tenant resolve — at the latest, the migration step
    ///     of the explicit import itself resolves the tenant — finds "installed older than embedded" and upgrades it
    ///     back. That is the guard's upgrade rule, not a downgrade.
    /// </summary>
    [Fact]
    public async Task ExplicitSystemDowngrade_IsAllowedWithWarning_AndTheEmbeddedSystemUpgradesItBack()
    {
        await WithTenantAsync("guardolder", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            Assert.True(EmbeddedSystem.Version.Minor > 0, "test needs a System version below the embedded one");
            var older = new CkModelId("System",
                new CkVersion(EmbeddedSystem.Version.Major, EmbeddedSystem.Version.Minor - 1, 0));

            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, older));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "Explicit downgrade", "'System'", tenantId,
                EmbeddedSystem.FullName, older.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ExplicitDowngradeCounterName, ("model", "System")) >= 1);

            await ResolveAsync(tenantId);
            Assert.Equal(EmbeddedSystem.FullName, await InstalledAsync(tenantId, "System"));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "Restoring system CK Model", tenantId));
            // The restore of a child tenant notifies the CHILD tenant, not the system tenant (review I1).
            Assert.Contains(tenantId, fixture.Notifications.UpdatedTenantIds);
        });
    }

    [Fact]
    public async Task EmbeddedSystem_SameVersion_ShortCircuits()
    {
        await WithTenantAsync("guardequal", async (_, tenantId) =>
        {
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await ResolveAsync(tenantId);
            await ResolveAsync(tenantId);

            Assert.Equal(EmbeddedSystem.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
        });
    }

    /// <summary>
    ///     A service model through the embedded/startup overload <c>ImportCkModelAsync(CkModelId, ...)</c> — the path
    ///     of every service's <c>DefaultConfigurationCreatorService</c> — and through the explicit overload.
    /// </summary>
    [Fact]
    public async Task ServiceModel_EmbeddedOverloadIsGuarded_ExplicitOverloadMayDowngrade()
    {
        await WithTenantAsync("guardsvc", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, result);
            Assert.False(result.HasErrors);
            var newer = Bump(TestV1ModelId, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(TestV1ModelId, newer));
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "Test"));

            // Embedded: guarded.
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await tenant.ImportCkModelAsync(TestV1ModelId, result = new OperationResult());
            await tenant.ImportCkModelWithDowngradeGuardAsync(TestV1ModelId);
            Assert.False(result.HasErrors);
            // The skip is reported to the caller as a warning (G-M1), so e.g. a blueprint install can name it.
            Assert.Contains(result.Messages, m => m.MessageLevel == MessageLevel.Warning &&
                                                  m.MessageText.Contains(newer.FullName, StringComparison.Ordinal));
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "Test"));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.True(counters.Sum(CkModelImportDiagnostics.EmbeddedImportSkippedCounterName,
                ("model", "Test"), ("reason", CkModelImportDiagnostics.ReasonNewerInstalled)) >= 1);
            // G-L2: the same prevented downgrade is logged at INFO once per process, repeats at DEBUG only.
            Assert.Single(fixture.Logs.Find(LogLevel.Information, "downgrade prevented", tenantId, newer.FullName));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Debug, "downgrade prevented", tenantId, newer.FullName));

            // Explicit: downgrade allowed, WARN + counter.
            var compiledV1 = await fixture.GetService<ICatalogService>().GetAsync(TestV1ModelId, new OperationResult());
            await tenant.ImportCkModelAsync(compiledV1!);
            Assert.Equal(TestV1ModelId.FullName, await InstalledAsync(tenantId, "Test"));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "Explicit downgrade", "'Test'", newer.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ExplicitDowngradeCounterName, ("model", "Test")) >= 1);
        });
    }

    /// <summary>
    ///     R2-3 (the Basic.Accounting scenario of Phase 0 E2E run 2) with the v1 contract made visible: an exact-pinned
    ///     model goes <c>ResolveFailed</c> after a System bump, stays there (no downgrade by the embedded System any
    ///     more, no flapping on unrelated imports) and returns to <c>Available</c> by itself after the import that makes
    ///     its pin satisfiable again.
    /// </summary>
    [Fact]
    public async Task ResolveFailedModel_ReturnsToAvailable_WhenItsPinBecomesSatisfiable()
    {
        await WithTenantAsync("revalid", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, result);
            Assert.False(result.HasErrors);
            Assert.Equal(1, await StateAsync(tenantId, TestV1ModelId)); // Available

            // Additive System bump: Test-1.0.0 pins the embedded System exactly → ResolveFailed.
            var newer = Bump(EmbeddedSystem, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, newer));
            Assert.Equal(2, await StateAsync(tenantId, TestV1ModelId)); // ResolveFailed

            // The embedded System no longer reverts the bump, so the model stays ResolveFailed ...
            await ResolveAsync(tenantId);
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal(2, await StateAsync(tenantId, TestV1ModelId));

            // ... and an unrelated import re-validates it without flipping it (pin still unmet).
            var stillFailedBefore = counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultStillFailed));
            await tenant.ImportCkModelAsync(Unrelated(newer));
            Assert.Equal(2, await StateAsync(tenantId, TestV1ModelId));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultStillFailed)) > stillFailedBefore);

            // The import that satisfies the pin again heals it — no manual action.
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, EmbeddedSystem));
            Assert.Equal(1, await StateAsync(tenantId, TestV1ModelId));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultRecovered)) >= 1);
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "resolves again", TestV1ModelId.FullName));

            // Its element rows were flipped back too, so the cache rebuilt from Mongo has its types again.
            var typeStates = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkType")
                .Find(new BsonDocument("ckModelId", TestV1ModelId.FullName))
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.NotEmpty(typeStates);
            Assert.All(typeStates, t => Assert.Equal(1, t["modelState"].AsInt32));
        });
    }

    /// <summary>
    ///     G-H1: two services import different embedded versions of the same model at the same time (parallel stack
    ///     start, rolling update, fresh tenant). Whoever waits for the import lock must repeat the decision under the
    ///     lock — the newer version always wins, in either order.
    /// </summary>
    [Fact]
    public async Task ConcurrentEmbeddedImports_NewerVersionAlwaysWins()
    {
        for (var round = 0; round < 3; round++)
        {
            await WithTenantAsync("guardrace", async (tenant, tenantId) =>
            {
                var older = tenant.ImportCkModelAsync(TestV1ModelId, new OperationResult());
                var newer = tenant.ImportCkModelAsync(TestV2ModelId, new OperationResult());
                await Task.WhenAll(older, newer);

                Assert.Equal(TestV2ModelId.FullName, await InstalledAsync(tenantId, "Test"));
                Assert.Equal(1, await StateAsync(tenantId, TestV2ModelId));
            });
        }
    }

    /// <summary>
    ///     G-H1, deterministic: the decision under the lock. Test-2.0.0 is installed after the caller's early check
    ///     (simulated by calling the repository directly); the guarded import of Test-1.0.0 must skip and report it.
    /// </summary>
    [Fact]
    public async Task GuardedImport_UnderTheLock_SkipsWhenANewerVersionWasInstalledMeanwhile()
    {
        await WithTenantAsync("guardlock", async (tenant, tenantId) =>
        {
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV2ModelId, result);
            Assert.False(result.HasErrors);

            var compiledV1 = await fixture.GetService<ICatalogService>().GetAsync(TestV1ModelId, new OperationResult());
            var identifier = NewSourceIdentifier(tenantId) with { GuardAgainstDowngrade = true };
            await fixture.GetService<IDatabaseCkModelRepository>().UpdateModelAsync(compiledV1!, identifier);

            Assert.True(identifier.ImportOutcome.SkippedUnderLock);
            Assert.Equal(TestV2ModelId, identifier.ImportOutcome.InstalledModelId);
            Assert.Equal(TestV2ModelId.FullName, await InstalledAsync(tenantId, "Test"));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "detected under the import lock", tenantId));

            // Unguarded (explicit) calls keep the old behaviour: they replace the model.
            var explicitIdentifier = NewSourceIdentifier(tenantId);
            await fixture.GetService<IDatabaseCkModelRepository>().UpdateModelAsync(compiledV1!, explicitIdentifier);
            Assert.False(explicitIdentifier.ImportOutcome.SkippedUnderLock);
            Assert.Equal(TestV1ModelId.FullName, await InstalledAsync(tenantId, "Test"));
        });
    }

    /// <summary>
    ///     G-H2: the system database keeps a NEWER System (another service upgraded it, the guard keeps it). A service
    ///     embedding the older System must still see the system tenant as existing and resolve tenants, and its
    ///     EnsureSystemCkModelAsync must not touch the newer System.
    /// </summary>
    [Fact]
    public async Task SystemDatabaseWithNewerSystem_StillExists_ForAServiceWithAnOlderEmbeddedSystem()
    {
        var systemContext = fixture.GetSystemContext();
        var newer = Bump(EmbeddedSystem, minor: 1);
        try
        {
            await systemContext.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, newer));
            Assert.False(await systemContext.IsCkModelExistingAsync(EmbeddedSystem));
            Assert.True(await systemContext.IsCkModelSatisfiedAsync(EmbeddedSystem));

            Assert.True(await systemContext.IsSystemTenantExistingAsync());
            await systemContext.EnsureSystemCkModelAsync();
            Assert.True(await systemContext.IsCkModelExistingAsync(newer));
            Assert.NotNull(await systemContext.TryFindTenantContextAsync(systemContext.TenantId));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "downgrade prevented", newer.FullName));
        }
        finally
        {
            // Restore the shared system tenant to the embedded System (explicit import may downgrade).
            await systemContext.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, EmbeddedSystem));
        }

        Assert.True(await systemContext.IsCkModelExistingAsync(EmbeddedSystem));
    }

    /// <summary>
    ///     G-M1 + G-M2: a stale ResolveFailed model at the service's own version (dependencies satisfiable again, e.g.
    ///     a tenant left behind before F1.0) is healed by the service's embedded import WITHOUT a re-import, and its
    ///     collection roots and indexes are restored.
    /// </summary>
    [Fact]
    public async Task StaleResolveFailedModel_IsHealedByTheEmbeddedImport_WithCollectionsAndIndexes()
    {
        await WithTenantAsync("staleheal", async (tenant, tenantId) =>
        {
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, result);
            Assert.False(result.HasErrors);
            var database = GetTenantDatabase(tenantId);
            var before = await CollectionIndexesAsync(database);
            var rootCollection = before.Keys.First(k => k.StartsWith("RtEntity_", StringComparison.Ordinal) &&
                                                        k.Contains("Test", StringComparison.Ordinal));

            // A stale ResolveFailed model whose dependencies are fine, and a lost (empty) collection root.
            await SetModelStateAsync(database, TestV1ModelId, 2);
            await database.DropCollectionAsync(rootCollection, TestContext.Current.CancellationToken);

            using var counters = new CkCounterRecorder();
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await tenant.ImportCkModelAsync(TestV1ModelId, result = new OperationResult());

            Assert.Equal(1, await StateAsync(tenantId, TestV1ModelId));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultRecovered)) >= 1);
            // A recovery is announced as one paired Pre/Post (other services reload their CK caches).
            Assert.Equal((pre + 1, pos + 1), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.Equal(before, await CollectionIndexesAsync(database));

            // Nothing left to heal: the next call changes nothing and sends nothing.
            await tenant.ImportCkModelAsync(TestV1ModelId, new OperationResult());
            Assert.Equal((pre + 1, pos + 1), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
        });
    }

    /// <summary>
    ///     G-L1: an embedded import whose dependencies are not installed is skipped BEFORE the Pre notification (no
    ///     unpaired Pre, no Post): here Test-1.0.0 pins the embedded System exactly while the tenant has a newer one.
    /// </summary>
    [Fact]
    public async Task EmbeddedImportWithMissingDependency_SendsNoNotification()
    {
        await WithTenantAsync("guarddeps", async (tenant, tenantId) =>
        {
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, Bump(EmbeddedSystem, minor: 1)));

            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, result);

            Assert.False(result.HasErrors);
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.Empty(await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
                .Find(new BsonDocument("modelId", "Test")).ToListAsync(TestContext.Current.CancellationToken));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "due to missing dependencies", tenantId,
                EmbeddedSystem.FullName));
        });
    }

    /// <summary>
    ///     D-G1-1 (G1 E2E): a service whose embedded System is OLDER than the tenant's (the state the downgrade guard
    ///     keeps) must still write entities. <c>AutoIncrementModifier</c> looked up the generated versioned
    ///     <c>System-&lt;embedded&gt;/AutoIncrement-1</c> on every insert, which is not in the cache of a tenant with a
    ///     newer System. One batch with a type WITHOUT auto-increment first and one WITH it second (the first entry
    ///     used to end the whole batch's auto-increment pass).
    /// </summary>
    [Fact]
    public async Task OlderEmbeddedSystem_CanWriteEntities_IntoATenantWithANewerSystem()
    {
        await WithTenantAsync("guardwrite", async (tenant, tenantId) =>
        {
            var newer = Bump(EmbeddedSystem, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, newer));
            await tenant.ImportCkModelAsync(TicketModel(newer));
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));

            var repository = tenant.GetTenantRepository();
            using (var session = await repository.GetSessionAsync())
            {
                session.StartTransaction();
                var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
                counter.RtWellKnownName = "TicketNumber";
                counter.CurrentValue = 41;
                counter.End = 1000;
                await repository.InsertOneRtEntityAsync(session, counter);
                await session.CommitTransactionAsync();
            }

            var noteId = OctoObjectId.GenerateNewId();
            var ticketId = OctoObjectId.GenerateNewId();
            using (var session = await repository.GetSessionAsync())
            {
                session.StartTransaction();
                var operationResult = new OperationResult();
                await repository.ApplyChangesAsync(session, new List<IEntityUpdateInfo<RtEntity>>
                {
                    EntityUpdateInfo<RtEntity>.CreateInsert(new RtEntity(NoteTypeId, noteId,
                        new Dictionary<string, object?> { { "Title", "no auto-increment" } })),
                    EntityUpdateInfo<RtEntity>.CreateInsert(new RtEntity(TicketTypeId, ticketId,
                        new Dictionary<string, object?> { { "Title", "with auto-increment" } }))
                }, operationResult);
                Assert.False(operationResult.HasErrors, operationResult.GetMessages());
                await session.CommitTransactionAsync();
            }

            var database = GetTenantDatabase(tenantId);
            var documents = new List<BsonDocument>();
            foreach (var name in await (await database.ListCollectionNamesAsync(
                         cancellationToken: TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken))
            {
                if (name.StartsWith("RtEntity_", StringComparison.Ordinal))
                {
                    documents.AddRange(await database.GetCollection<BsonDocument>(name)
                        .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray
                        {
                            ObjectId.Parse(noteId.ToString()), ObjectId.Parse(ticketId.ToString())
                        }))).ToListAsync(TestContext.Current.CancellationToken));
                }
            }

            Assert.Equal(2, documents.Count);
            var ticket = documents.Single(d => d["_id"].AsObjectId.ToString() == ticketId.ToString());
            Assert.Equal(42, ticket["attributes"]["number"].ToInt64());
            Assert.False(documents.Single(d => d["_id"].AsObjectId.ToString() == noteId.ToString())["attributes"]
                .AsBsonDocument.Contains("number"));
        });
    }

    private static readonly RtCkId<CkTypeId> NoteTypeId = new("GuardWrite/Note");
    private static readonly RtCkId<CkTypeId> TicketTypeId = new("GuardWrite/Ticket");

    /// <summary>Note (no auto-increment) and Ticket (Number from the "TicketNumber" counter), on System <paramref name="system" />.</summary>
    private static CkCompiledModelRoot TicketModel(CkModelId system)
    {
        var id = new CkModelId("GuardWrite-1.0.0");
        CkTypeAttributeDto Title() => new()
        {
            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Title-1")), AttributeName = "Title",
            IsOptional = true
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "D-G1-1 entity writes on a newer System",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Number-1"), ValueType = AttributeValueTypesDto.Int }
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Note-1"), IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
                    Attributes = [Title()]
                },
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Ticket-1"), IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
                    Attributes =
                    [
                        Title(),
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Number-1")),
                            AttributeName = "Number", IsOptional = true, AutoIncrementReference = "TicketNumber"
                        }
                    ]
                }
            ]
        };
    }

    private static async Task SetModelStateAsync(IMongoDatabase database, CkModelId modelId, int state)
    {
        await database.GetCollection<BsonDocument>("CkModel").UpdateOneAsync(new BsonDocument("_id", modelId.FullName),
            new BsonDocument("$set", new BsonDocument("modelState", state)),
            cancellationToken: TestContext.Current.CancellationToken);
        foreach (var collection in new[]
                 {
                     "CkType", "CkAttribute", "CkRecord", "CkEnum", "CkAssociationRole", "CkTypeAssociation",
                     "CkTypeInheritance", "CkRecordInheritance"
                 })
        {
            await database.GetCollection<BsonDocument>(collection).UpdateManyAsync(
                new BsonDocument("ckModelId", modelId.FullName),
                new BsonDocument("$set", new BsonDocument("modelState", state)),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>RtEntity_* collections with their sorted index names.</summary>
    private static async Task<Dictionary<string, string>> CollectionIndexesAsync(IMongoDatabase database)
    {
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        var result = new Dictionary<string, string>();
        foreach (var name in names.Where(n => n.StartsWith("RtEntity_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes
                    .ListAsync(TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken);
            result[name] = string.Join(",", indexes.Select(i => i["name"].AsString).Order(StringComparer.Ordinal));
        }

        return result;
    }

    private TenantDatabaseSourceIdentifier NewSourceIdentifier(string tenantId)
    {
        var databaseName = tenantId.ToLowerInvariant();
        var client = fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName);
        var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance, client,
            databaseName, tenantId);
        return new TenantDatabaseSourceIdentifier(null, dataSource, tenantId);
    }

    private static CkModelId Bump(CkModelId id, int minor) =>
        new(id.Name, new CkVersion(id.Version.Major, id.Version.Minor + minor, 0));

    private static CkCompiledModelRoot Unrelated(CkModelId system) => new()
    {
        ModelId = new CkModelId("GuardUnrelated-1.0.0"),
        Description = "AB#5901 unrelated import that triggers a re-validation",
        Dependencies = [system],
        Attributes = [new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String }]
    };

    /// <summary>The compiled catalog model of <paramref name="source" /> with every occurrence of its id rewritten.</summary>
    private async Task<CkCompiledModelRoot> RenameAsync(CkModelId source, CkModelId target)
    {
        var compiled = await fixture.GetService<ICatalogService>().GetAsync(source, new OperationResult());
        Assert.NotNull(compiled);
        var serializer = fixture.GetService<ICkJsonSerializer>();
        using var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await serializer.SerializeAsync(writer, compiled);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        var json = Encoding.UTF8.GetString(stream.ToArray()).Replace(source.FullName, target.FullName);
        var operationResult = new OperationResult();
        var renamed = await serializer.DeserializeCompiledModelRootAsync(json, "renamed", operationResult);
        Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));
        return renamed;
    }

    private async Task ResolveAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task WithTenantAsync(string prefix, Func<ITenantContext, string, Task> body)
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

            await body(tenant, tenantId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private async Task<string> InstalledAsync(string tenantId, string modelName)
    {
        var documents = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
            .Find(new BsonDocument("modelId", modelName)).ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(documents)["_id"].AsString;
    }

    private async Task<int> StateAsync(string tenantId, CkModelId modelId)
    {
        var document = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
            .Find(new BsonDocument("_id", modelId.FullName)).SingleAsync(TestContext.Current.CancellationToken);
        return document["modelState"].AsInt32;
    }

    private IMongoDatabase GetTenantDatabase(string tenantId)
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
        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(tenantId.ToLowerInvariant());
    }
}
