using System.Collections.Concurrent;
using System.Diagnostics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#9302: the tiebreak index upgrade holds <c>index_update_lock</c> only while it works on one collection. Between two
///     collections the lock is free; an index update that waits for it (a CK import) gets it after at most the current
///     collection and succeeds; the upgrade then finishes the remaining collections in the same run, from re-read CK
///     metadata. Cancellation stops before the next collection, a lost lock aborts the current one, and the lock is not
///     held afterwards. Every index state tracking scope lies inside one lock section.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class TiebreakIndexUpgradeLockTests(ImportTestCkModelFixture fixture)
{
    private const string ModelName = "LockModel";
    private const string NewModelName = "LockNewModel";
    private const string FieldName = "SimpleField";
    private const string FieldPath = "attributes.simpleField";
    private const string OtherFieldName = "OtherField";
    private const string OtherFieldPath = "attributes.otherField";
    private static readonly string[] TypeNames = ["Alpha", "Beta", "Gamma"];

    private static readonly string[] LegacyKeys = ["ckTypeId", FieldPath, "rtState"];
    private static readonly string[] TiebreakKeys = ["ckTypeId", FieldPath, "_id", "rtState"];
    private static readonly string[] OtherTiebreakKeys = ["ckTypeId", OtherFieldPath, "_id", "rtState"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     AC 1 and AC 3: every collection is upgraded in a lock section of its own - the lock is held from the
    ///     acquisition to the release of each section and free after it; no index state tracking scope is open when the
    ///     section gets or gives up the lock. Without a waiter the upgrade never yields.
    /// </summary>
    [Fact]
    public async Task Upgrade_HoldsTheLockPerCollection_FreeBetweenCollections()
    {
        await WithLegacyTenantAsync(async (tenant, systemContext) =>
        {
            var dataSource = DataSourceOf(tenant);
            var events = new List<(IndexUpgradeSectionEvent Event, bool Held)>();
            dataSource.UpgradeSectionObserver = async e => events.Add((e, await IsLockHeldAsync(tenant)));

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, Ct);
            }

            var sections = events.GroupBy(e => e.Event.Section).ToList();
            Assert.True(sections.Count >= TypeNames.Length + 1, $"only {sections.Count} section(s)");
            foreach (var section in sections.Where(s => s.Key < sections.Count - 1))
            {
                Assert.Equal(
                    [IndexUpgradeSectionStage.Acquired, IndexUpgradeSectionStage.TrackingStarted,
                        IndexUpgradeSectionStage.Releasing, IndexUpgradeSectionStage.Released,
                        IndexUpgradeSectionStage.Yielded],
                    section.Select(e => e.Event.Stage));
                Assert.Single(section.Select(e => e.Event.CollectionName).Distinct());
                Assert.NotNull(section.First().Event.CollectionName);
                // The tracking scope is open only inside the section, never when it gets or gives up the lock.
                Assert.All(section, e => Assert.Equal(e.Event.Stage == IndexUpgradeSectionStage.TrackingStarted,
                    e.Event.TrackingActive));
                Assert.All(section, e => Assert.False(e.Event.WaiterSeen));
                Assert.Equal([true, true, true, false, false], section.Select(e => e.Held));
            }

            // The last section creates the model-independent indexes.
            var last = sections[^1].ToList();
            Assert.All(last, e => Assert.Null(e.Event.CollectionName));
            Assert.False(last[^1].Held);

            // Each collection once, ours among them.
            var collections = events.Where(e => e.Event.Stage == IndexUpgradeSectionStage.Acquired)
                .Select(e => e.Event.CollectionName).OfType<string>().ToList();
            Assert.Equal(collections.Count, collections.Distinct().Count());
            Assert.Superset(CollectionNames(ModelName).ToHashSet(), collections.ToHashSet());

            foreach (var typeName in TypeNames)
            {
                Assert.Single(await IndexesAsync(Collection(tenant, ModelName, typeName)),
                    i => KeysOf(i).SequenceEqual(TiebreakKeys));
            }
        });
    }

    /// <summary>
    ///     AC 2, AC 3, AC 4 and the coordinator's conditions 4 and 5: a CK import starts while the upgrade works on one of
    ///     our collections and waits for the lock. It gets the lock right after that collection (before the next
    ///     section), succeeds without a <see cref="TimeoutException" />, and changes the remaining collections (a second
    ///     index on every type) and creates a new collection. The upgrade then finishes every remaining collection in the
    ///     same run from the re-read metadata: the import's new indexes are kept, every legacy index is replaced by its
    ///     tiebreak index (built before the legacy one is dropped), the new collection is processed, and a second upgrade
    ///     changes nothing.
    /// </summary>
    [Fact]
    public async Task ImportDuringUpgrade_GetsTheLockAfterTheCurrentCollection_AndTheUpgradeFinishesTheRest()
    {
        await WithLegacyTenantAsync(async (tenant, systemContext) =>
        {
            var dataSource = DataSourceOf(tenant);
            var ours = CollectionNames(ModelName).ToHashSet();
            var events = new ConcurrentQueue<IndexUpgradeSectionEvent>();
            Task? import = null;
            var importAwaited = false;
            int? waitedInSection = null;
            List<string> waiterIds = [];
            int? waiterGoneAtSection = null;
            var sectionStopwatches = new Dictionary<string, Stopwatch>();
            dataSource.UpgradeSectionObserver = async e =>
            {
                events.Enqueue(e);
                if (e.Stage == IndexUpgradeSectionStage.Acquired && import == null && e.CollectionName != null &&
                    ours.Contains(e.CollectionName))
                {
                    import = Task.Run(async () =>
                    {
                        await tenant.ImportCkModelAsync(Model(ModelName, "1.0.1", withOtherIndex: true));
                        await tenant.ImportCkModelAsync(Model(NewModelName, "1.0.0", typeNames: ["Delta"]));
                    }, Ct);

                    // The import's index update waits for the lock this section holds: its waiter marker shows it.
                    var waited = Stopwatch.StartNew();
                    while ((waiterIds = await WaiterIdsAsync(tenant)).Count == 0)
                    {
                        Assert.False(import.IsFaulted, import.Exception?.ToString());
                        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "the import never waited for the lock");
                        await Task.Delay(50, Ct);
                    }

                    waitedInSection = e.Section;
                }
                else if (e.Stage == IndexUpgradeSectionStage.Acquired && e.Section > waitedInSection &&
                         waiterGoneAtSection == null)
                {
                    // The lock is ours again. The yield gives the import no guaranteed turn, so it may take the lock only
                    // after a later section: check at every later section until its waiter marker is gone (a waiter
                    // deletes its marker right after it acquired; one that timed out would delete it too, but the import
                    // is asserted to succeed below).
                    var now = await WaiterIdsAsync(tenant);
                    if (!waiterIds.Intersect(now).Any())
                    {
                        waiterGoneAtSection = e.Section;
                    }
                }
                else if (e.Stage == IndexUpgradeSectionStage.Released && !importAwaited && e.Section > waitedInSection &&
                         import != null)
                {
                    // Barrier outside the lock, at the first release after the section the import waited for (that
                    // section still yields to it first): both imports - also the second one, a new collection - are done
                    // before the next section reads the metadata, so the run must pick the new collection up. It does
                    // not depend on how fast the first import's tail (model Available, re-validation) is.
                    importAwaited = true;
                    await import;
                }

                if (e.CollectionName != null)
                {
                    if (e.Stage == IndexUpgradeSectionStage.Acquired)
                    {
                        sectionStopwatches[e.CollectionName] = Stopwatch.StartNew();
                    }
                    else if (e.Stage == IndexUpgradeSectionStage.Releasing)
                    {
                        sectionStopwatches[e.CollectionName].Stop();
                    }
                }
            };

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, Ct);
            }

            Assert.NotNull(import);
            await import; // no TimeoutException
            var list = events.ToList();
            var firstOurs = list.First(e => e.Stage == IndexUpgradeSectionStage.Acquired && ours.Contains(e.CollectionName!));

            // The upgrade yielded after the section the import waited for, and the import's index update took the lock
            // at a later section boundary (its waiter marker was gone at a later section) - observed through the lock,
            // not timed; the yield gives no guaranteed turn, so not necessarily at the very next section.
            Assert.Equal(firstOurs.Section, waitedInSection);
            Assert.Contains(list, e => e.Stage == IndexUpgradeSectionStage.Yielded && e.Section == firstOurs.Section &&
                                       e.WaiterSeen);
            Assert.True(waiterGoneAtSection > waitedInSection,
                "the import's waiter marker was still there at every later section of the run");
            Assert.True(importAwaited, "the barrier for the imports was not reached: no section after the waited one");

            // The remaining collections, the new collection and the model-independent indexes in the same run.
            var acquired = list.Where(e => e.Stage == IndexUpgradeSectionStage.Acquired)
                .Select(e => e.CollectionName).ToList();
            Assert.Superset(ours, acquired.OfType<string>().ToHashSet());
            Assert.Contains(CollectionNames(NewModelName, ["Delta"]).Single(), acquired);
            Assert.Null(acquired[^1]);
            Assert.All(list, e => Assert.Equal(e.Stage == IndexUpgradeSectionStage.TrackingStarted, e.TrackingActive));

            var before = new Dictionary<string, List<string>>();
            foreach (var typeName in TypeNames)
            {
                var indexes = await IndexesAsync(Collection(tenant, ModelName, typeName));
                Assert.Single(indexes, i => KeysOf(i).SequenceEqual(TiebreakKeys));
                Assert.Single(indexes, i => KeysOf(i).SequenceEqual(OtherTiebreakKeys));
                Assert.DoesNotContain(indexes, i => KeysOf(i).SequenceEqual(LegacyKeys));
                before[typeName] = Describe(indexes);
            }

            Assert.Single(await IndexesAsync(Collection(tenant, NewModelName, "Delta")),
                i => KeysOf(i).SequenceEqual(TiebreakKeys));

            // Idempotent: a second upgrade changes nothing.
            dataSource.UpgradeSectionObserver = null;
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, Ct);
            }

            foreach (var typeName in TypeNames)
            {
                Assert.Equal(before[typeName], Describe(await IndexesAsync(Collection(tenant, ModelName, typeName))));
            }

            RunProgress.Report("AB#9302 sections: " + string.Join(", ", sectionStopwatches
                .Where(kvp => ours.Contains(kvp.Key))
                .Select(kvp => $"{kvp.Key} {kvp.Value.Elapsed.TotalMilliseconds:F0} ms")));
        });
    }

    /// <summary>
    ///     Coordinator's condition 3: a waiter marker whose waiter is gone. An expired marker is never seen. A fresh one
    ///     (written right after the first section) makes the upgrade yield for at most
    ///     <see cref="MongoDbRepositoryDataSource.YieldToWaiterMaxWait" /> (3 s, nobody takes the lock); the marker expires
    ///     after <see cref="RepositoryDistributedLockService.WaiterMarkerTimeToLive" /> (5 s), so at most two yields see it,
    ///     whatever the speed of the sections, and the run completes. No wall-clock bound is asserted.
    /// </summary>
    [Fact]
    public async Task DeadWaiter_TheUpgradeContinuesWithoutWaiting()
    {
        await WithLegacyTenantAsync(async (tenant, systemContext) =>
        {
            var dataSource = DataSourceOf(tenant);
            var locks = SysLockCollection(tenant);
            await locks.InsertOneAsync(new SysLock
            {
                Id = $"{RepositoryDistributedLockService.WaiterIdPrefix}{MongoDbRepositoryDataSource.IndexUpdateLockId}:expired",
                ExpiryDateTime = DateTime.UtcNow.AddSeconds(-1)
            }, cancellationToken: Ct);

            var yields = new List<IndexUpgradeSectionEvent>();
            var deadInserted = false;
            dataSource.UpgradeSectionObserver = async e =>
            {
                if (e.Stage == IndexUpgradeSectionStage.Released && !deadInserted)
                {
                    // A waiter that wrote its marker and died right away.
                    deadInserted = true;
                    await locks.InsertOneAsync(new SysLock
                    {
                        Id = $"{RepositoryDistributedLockService.WaiterIdPrefix}" +
                             $"{MongoDbRepositoryDataSource.IndexUpdateLockId}:dead",
                        ExpiryDateTime = DateTime.UtcNow.Add(RepositoryDistributedLockService.WaiterMarkerTimeToLive)
                    }, cancellationToken: Ct);
                }
                else if (e.Stage == IndexUpgradeSectionStage.Yielded)
                {
                    yields.Add(e);
                }
            };

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, Ct);
            }

            Assert.True(yields[0].WaiterSeen, "the fresh marker was not seen");
            var seen = yields.Count(y => y.WaiterSeen);
            Assert.InRange(seen, 1, 2);
            Assert.True(yields.Count >= TypeNames.Length);
            RunProgress.Report($"AB#9302 dead waiter: {yields.Count} yields, {seen} saw the marker");

            foreach (var typeName in TypeNames)
            {
                Assert.Single(await IndexesAsync(Collection(tenant, ModelName, typeName)),
                    i => KeysOf(i).SequenceEqual(TiebreakKeys));
            }
        });
    }

    /// <summary>AC 5: the caller's token stops the upgrade before the next collection; the lock is free afterwards.</summary>
    [Fact]
    public async Task Cancellation_StopsBeforeTheNextCollection_AndTheLockIsFree()
    {
        await WithLegacyTenantAsync(async (tenant, systemContext) =>
        {
            var dataSource = DataSourceOf(tenant);
            var ours = CollectionNames(ModelName).ToHashSet();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var events = new List<IndexUpgradeSectionEvent>();
            dataSource.UpgradeSectionObserver = e =>
            {
                events.Add(e);
                if (e.Stage == IndexUpgradeSectionStage.Releasing && ours.Contains(e.CollectionName ?? ""))
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            };

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    dataSource.UpgradeTiebreakIndexesAsync(adminSession, cts.Token));
            }

            // The collection that was cancelled in finished; no section started after it.
            var cancelledIn = events.Single(e => e.Stage == IndexUpgradeSectionStage.Releasing &&
                                                 ours.Contains(e.CollectionName ?? ""));
            Assert.DoesNotContain(events, e => e.Section > cancelledIn.Section &&
                                               e.Stage == IndexUpgradeSectionStage.Acquired);
            // The collection the cancellation came in is upgraded; our other two collections are not.
            foreach (var typeName in TypeNames)
            {
                var indexes = await IndexesAsync(Collection(tenant, ModelName, typeName));
                var upgraded = CollectionNames(ModelName, [typeName]).Single() == cancelledIn.CollectionName;
                Assert.Equal(upgraded, indexes.Any(i => KeysOf(i).SequenceEqual(TiebreakKeys)));
                Assert.Equal(!upgraded, indexes.Any(i => KeysOf(i).SequenceEqual(LegacyKeys)));
            }

            Assert.False(await IsLockHeldAsync(tenant));
        });
    }

    /// <summary>
    ///     AC 5: a lost lock (another holder claimed it; the heartbeat notices within 15 s) during a collection run, after
    ///     its index state tracking began, aborts the collection before its first index operation, persists none of its
    ///     index states (the stored states of its types are unchanged), and the upgrade does not go on with the next
    ///     collection. The upgrade does not delete the other holder's lock document.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task LockLost_AbortsTheCurrentCollection_AndTheNextIsNotStarted()
    {
        // xUnit1069: a test with a Timeout must reference TestContext.Current.CancellationToken directly.
        var ct = TestContext.Current.CancellationToken;
        await WithLegacyTenantAsync(async (tenant, systemContext) =>
        {
            var dataSource = DataSourceOf(tenant);
            var ours = CollectionNames(ModelName).ToHashSet();
            var locks = SysLockCollection(tenant);
            var thiefToken = Guid.NewGuid();
            var events = new List<IndexUpgradeSectionEvent>();
            string? stolenIn = null;
            var statesBefore = await StatesAsync(tenant);
            dataSource.UpgradeSectionObserver = async e =>
            {
                events.Add(e);
                if (e.Stage != IndexUpgradeSectionStage.TrackingStarted || stolenIn != null ||
                    !ours.Contains(e.CollectionName ?? ""))
                {
                    return;
                }

                Assert.True(e.TrackingActive);

                stolenIn = e.CollectionName;
                await locks.UpdateOneAsync(
                    Builders<SysLock>.Filter.Eq(s => s.Id, MongoDbRepositoryDataSource.IndexUpdateLockId),
                    Builders<SysLock>.Update.Set(s => s.OwnerToken, thiefToken).Set(s => s.HolderInfo, "test/thief"),
                    cancellationToken: ct);
                var lost = new TaskCompletionSource();
                await using var registration = e.LockLostToken.Register(() => lost.TrySetResult());
                Assert.Same(lost.Task, await Task.WhenAny(lost.Task, Task.Delay(TimeSpan.FromSeconds(30), ct)));
            };

            try
            {
                using (var adminSession = await systemContext.GetAdminSessionAsync())
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        dataSource.UpgradeTiebreakIndexesAsync(adminSession, ct));
                }

                Assert.NotNull(stolenIn);
                var section = events.Single(e => e.Stage == IndexUpgradeSectionStage.Acquired &&
                                                 e.CollectionName == stolenIn).Section;
                Assert.DoesNotContain(events, e => e.Section > section);
                Assert.DoesNotContain(events, e => e.Section == section && e.Stage == IndexUpgradeSectionStage.Releasing);
                Assert.Contains(events, e => e.Section == section && e.Stage == IndexUpgradeSectionStage.TrackingStarted);

                // No index state of the aborted collection's types was persisted outside the section.
                Assert.Equal(statesBefore, await StatesAsync(tenant));

                // The collection was not touched: its legacy index is still there, no tiebreak index was built.
                var typeName = TypeNames.Single(t => CollectionNames(ModelName, [t]).Single() == stolenIn);
                var indexes = await IndexesAsync(Collection(tenant, ModelName, typeName));
                Assert.Contains(indexes, i => KeysOf(i).SequenceEqual(LegacyKeys));
                Assert.DoesNotContain(indexes, i => KeysOf(i).SequenceEqual(TiebreakKeys));

                // The lock is the thief's, not ours.
                var doc = await locks.Find(s => s.Id == MongoDbRepositoryDataSource.IndexUpdateLockId)
                    .FirstOrDefaultAsync(ct);
                Assert.NotNull(doc);
                Assert.Equal(thiefToken, doc.OwnerToken);
            }
            finally
            {
                await locks.DeleteOneAsync(Builders<SysLock>.Filter.Eq(s => s.Id,
                    MongoDbRepositoryDataSource.IndexUpdateLockId), ct);
            }
        });
    }

    /// <summary>
    ///     Kassandra round 1, finding 1 (invariant decided by the coordinator): a CK import releases the lock after its
    ///     index update and makes its model Available only afterwards. A section that runs in that window must not drop or
    ///     replace an index of a collection holding a type of the Importing model. Here a model derives a type from Alpha
    ///     and defines an index on another field; its types are held in state Importing while the upgrade runs. Alpha's
    ///     collection is skipped, retried after the others and left for the next run: the derived type's index and the
    ///     legacy index stay, the other collections are upgraded. Once the model is Available the next upgrade upgrades
    ///     Alpha and keeps the derived type's index.
    /// </summary>
    [Fact]
    public async Task CollectionWithATypeOfAnImportingModel_IsLeftForTheNextRun_AndNoIndexOfItIsDropped()
    {
        await WithLegacyTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(DerivedModel());
            var alpha = Collection(tenant, ModelName, TypeNames[0]);
            var alphaName = CollectionNames(ModelName, [TypeNames[0]]).Single();
            Assert.Contains(await IndexesAsync(alpha), i => KeysOf(i).SequenceEqual(OtherTiebreakKeys));

            var ckTypes = ((TenantRepository)tenant.GetTenantRepository()).DataSource.CkTypes.GetMongoCollection();
            FilterDefinition<CkType> derivedFilter =
                new BsonDocument("_id", new BsonRegularExpression("^" + DerivedModelName + "-"));
            await ckTypes.UpdateManyAsync(derivedFilter,
                Builders<CkType>.Update.Set(t => t.ModelState, ModelState.Importing), cancellationToken: Ct);

            var dataSource = DataSourceOf(tenant);
            var events = new List<IndexUpgradeSectionEvent>();
            dataSource.UpgradeSectionObserver = e =>
            {
                events.Add(e);
                return Task.CompletedTask;
            };
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, Ct);
            }

            Assert.DoesNotContain(events, e => e.Stage == IndexUpgradeSectionStage.Acquired && e.CollectionName == alphaName);
            Assert.Contains(events, e => e.Stage == IndexUpgradeSectionStage.LeftForNextRun && e.CollectionName == alphaName);
            var alphaIndexes = await IndexesAsync(alpha);
            Assert.Contains(alphaIndexes, i => KeysOf(i).SequenceEqual(OtherTiebreakKeys));
            Assert.Contains(alphaIndexes, i => KeysOf(i).SequenceEqual(LegacyKeys));
            foreach (var typeName in TypeNames.Skip(1))
            {
                Assert.Single(await IndexesAsync(Collection(tenant, ModelName, typeName)),
                    i => KeysOf(i).SequenceEqual(TiebreakKeys));
            }

            // The import finished: the next upgrade upgrades Alpha and keeps the derived type's index.
            await ckTypes.UpdateManyAsync(derivedFilter,
                Builders<CkType>.Update.Set(t => t.ModelState, ModelState.Available), cancellationToken: Ct);
            dataSource.UpgradeSectionObserver = null;
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, Ct);
            }

            alphaIndexes = await IndexesAsync(alpha);
            Assert.Contains(alphaIndexes, i => KeysOf(i).SequenceEqual(OtherTiebreakKeys));
            Assert.Single(alphaIndexes, i => KeysOf(i).SequenceEqual(TiebreakKeys));
            Assert.DoesNotContain(alphaIndexes, i => KeysOf(i).SequenceEqual(LegacyKeys));
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private const string DerivedModelName = "LockDerivedModel";

    /// <summary>A model with a type derived from Alpha that defines an Ascending index on the other field.</summary>
    private static CkCompiledModelRoot DerivedModel()
    {
        const string modelVersion = DerivedModelName + "-1.0.0";
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId, new CkModelId($"{ModelName}-1.0.0")],
            Types =
            [
                new()
                {
                    TypeId = new CkTypeId("AlphaChild"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>($"{ModelName}-1.0.0/{TypeNames[0]}"),
                    Attributes = [],
                    Indexes =
                    [
                        new()
                        {
                            IndexType = IndexTypeDto.Ascending,
                            Fields = [new() { AttributePaths = [OtherFieldName] }]
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>The stored index states of every type of <see cref="ModelName" />, as comparable strings.</summary>
    private static async Task<List<string>> StatesAsync(ITenantContext tenant)
    {
        var ckTypes = ((TenantRepository)tenant.GetTenantRepository()).DataSource.CkTypes.GetMongoCollection();
        FilterDefinition<CkType> filter = new BsonDocument("_id", new BsonRegularExpression("^" + ModelName + "-"));
        var types = await ckTypes.Find(filter).ToListAsync(Ct);
        return types.SelectMany(t => (t.IndexStates ?? []).Select(s =>
                $"{t.CkTypeId}:{s.Name}:{s.State}:{s.ErrorMessage}:{s.AppliedAt?.Ticks}"))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>A tenant with <see cref="ModelName" /> imported and every one of its indexes in the legacy shape.</summary>
    private async Task WithLegacyTenantAsync(Func<ITenantContext, ISystemContext, Task> test)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"TL_{Guid.NewGuid():N}"[..20];
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenant = await systemContext.GetChildTenantContextAsync(tenantId);
            await tenant.ImportCkModelAsync(Model(ModelName, "1.0.0"));
            foreach (var typeName in TypeNames)
            {
                await MakeLegacyAsync(Collection(tenant, ModelName, typeName));
            }

            await test(tenant, systemContext);
        }
        finally
        {
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    private static MongoDbRepositoryDataSource DataSourceOf(ITenantContext tenant) =>
        (MongoDbRepositoryDataSource)((TenantRepository)tenant.GetTenantRepository()).DataSource;

    private static IMongoCollection<SysLock> SysLockCollection(ITenantContext tenant) =>
        Collection(tenant, ModelName, TypeNames[0]).Database
            .GetCollection<SysLock>(InfrastructureCollections.SysLock);

    private static async Task<bool> IsLockHeldAsync(ITenantContext tenant) =>
        await SysLockCollection(tenant)
            .Find(s => s.Id == MongoDbRepositoryDataSource.IndexUpdateLockId && s.ExpiryDateTime > DateTime.UtcNow)
            .AnyAsync(Ct);

    /// <summary>The ids of the fresh waiter markers of <c>index_update_lock</c>.</summary>
    private static async Task<List<string>> WaiterIdsAsync(ITenantContext tenant)
    {
        var prefix = $"{RepositoryDistributedLockService.WaiterIdPrefix}{MongoDbRepositoryDataSource.IndexUpdateLockId}:";
        var all = await SysLockCollection(tenant).Find(s => s.ExpiryDateTime > DateTime.UtcNow).ToListAsync(Ct);
        return all.Where(s => s.Id.StartsWith(prefix, StringComparison.Ordinal)).Select(s => s.Id).ToList();
    }

    private static IEnumerable<string> CollectionNames(string modelName, string[]? typeNames = null) =>
        (typeNames ?? TypeNames).Select(t => new RtCkId<CkTypeId>($"{modelName}/{t}").GetCkTypeCollectionName());

    private static CkCompiledModelRoot Model(string modelName, string version, bool withOtherIndex = false,
        string[]? typeNames = null)
    {
        var modelVersion = $"{modelName}-{version}";
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId(FieldName), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId(OtherFieldName), ValueType = AttributeValueTypesDto.String
                }
            ],
            Types = (typeNames ?? TypeNames).Select(typeName => new CkCompiledTypeDto
            {
                TypeId = new CkTypeId(typeName),
                DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                IsCollectionRoot = true,
                Attributes =
                [
                    new()
                    {
                        CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{FieldName}"),
                        AttributeName = FieldName
                    },
                    new()
                    {
                        CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{OtherFieldName}"),
                        AttributeName = OtherFieldName
                    }
                ],
                Indexes =
                [
                    new() { IndexType = IndexTypeDto.Ascending, Fields = [new() { AttributePaths = [FieldName] }] },
                    .. withOtherIndex
                        ? new List<CkTypeIndexDto>
                        {
                            new()
                            {
                                IndexType = IndexTypeDto.Ascending,
                                Fields = [new() { AttributePaths = [OtherFieldName] }]
                            }
                        }
                        : []
                ]
            }).ToList()
        };
    }

    private static IMongoCollection<BsonDocument> Collection(ITenantContext tenant, string modelName, string typeName)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var collection = repository.DataSource
            .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>($"{modelName}/{typeName}"))
            .GetMongoCollection();
        return collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
    }

    /// <summary>Replaces the tiebreak-shape index of the attribute with the legacy shape, under its legacy name.</summary>
    private static async Task MakeLegacyAsync(IMongoCollection<BsonDocument> collection)
    {
        var tiebreak = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(TiebreakKeys));
        var tiebreakName = tiebreak["name"].AsString;
        await collection.Indexes.DropOneAsync(tiebreakName);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "rtState", 1 } },
            new CreateIndexOptions { Name = tiebreakName[..^TiebreakIndexShape.NameSuffix.Length] }));
        CollectionIndexCache.Invalidate(collection);
    }

    private static async Task<List<BsonDocument>> IndexesAsync(IMongoCollection<BsonDocument> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static string[] KeysOf(BsonDocument index) => index["key"].AsBsonDocument.Names.ToArray();

    private static List<string> Describe(IEnumerable<BsonDocument> indexes) =>
        indexes.Select(i => $"{i["name"]}:{i["key"].ToJson()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
}
