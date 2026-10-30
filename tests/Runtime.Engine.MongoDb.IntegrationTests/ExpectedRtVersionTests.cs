using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#7769: optimistic concurrency on <c>rtVersion</c>. A replace or update that passes the version it read is
///     written only if the stored entity still has that version; otherwise nothing is written and the caller gets an
///     <see cref="RtVersionConflictException" />. Writes without an expected version are covered by the existing tests.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class ExpectedRtVersionTests(ImportTestCkModelFixture fixture)
{
    public enum WriteMode
    {
        Replace,
        Update
    }

    private async Task<(ITenantRepository Repository, OctoObjectId RtId)> InsertCounterAsync(int counter,
        string name = "initial")
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var entity = await repository.CreateTransientRtEntityAsync<RtSeedGuardEntity>();
        entity.RtId = rtId;
        entity.GuardCount = counter;
        entity.GuardText = name;
        await repository.InsertOneRtEntityAsync(session, entity);
        await session.CommitTransactionAsync();
        return (repository, rtId);
    }

    private static async Task<RtSeedGuardEntity> ReadAsync(ITenantRepository repository, OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var entity = await repository.GetRtEntityByRtIdAsync<RtSeedGuardEntity>(session, rtId);
        await session.CommitTransactionAsync();
        return Assert.IsType<RtSeedGuardEntity>(entity);
    }

    /// <summary>
    ///     Writes <paramref name="entity" /> (read at <paramref name="expectedRtVersion" />) in its own transaction.
    ///     <see cref="WriteMode.Update" /> sends only the changed attribute, <see cref="WriteMode.Replace" /> the
    ///     whole entity.
    /// </summary>
    private static async Task WriteAsync(ITenantRepository repository, OctoObjectId rtId, RtSeedGuardEntity entity,
        ulong expectedRtVersion, WriteMode mode)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        if (mode == WriteMode.Replace)
        {
            await repository.ReplaceOneRtEntityByIdAsync(session, rtId, entity, expectedRtVersion);
        }
        else
        {
            var partial = await repository.CreateTransientRtEntityAsync<RtSeedGuardEntity>();
            partial.GuardCount = entity.GuardCount;
            await repository.UpdateOneRtEntityByIdAsync(session, rtId, partial, expectedRtVersion);
        }

        await session.CommitTransactionAsync();
    }

    [Theory]
    [InlineData(WriteMode.Replace)]
    [InlineData(WriteMode.Update)]
    public async Task ExpectedVersionMatches_WriteSucceeds_VersionBecomesNPlusOne(WriteMode mode)
    {
        var (repository, rtId) = await InsertCounterAsync(1);
        var read = await ReadAsync(repository, rtId);

        var versionRead = read.RtVersion;
        read.GuardCount = 2;
        await WriteAsync(repository, rtId, read, versionRead, mode);

        var after = await ReadAsync(repository, rtId);
        Assert.Equal(2, after.GuardCount);
        Assert.Equal(versionRead + 1, after.RtVersion);
    }

    [Theory]
    [InlineData(WriteMode.Replace)]
    [InlineData(WriteMode.Update)]
    public async Task ExpectedVersionStale_ThrowsConflict_AndNothingIsWritten(WriteMode mode)
    {
        var (repository, rtId) = await InsertCounterAsync(1);
        var first = await ReadAsync(repository, rtId);
        var second = await ReadAsync(repository, rtId);

        // another writer moves the entity from n to n+1
        first.GuardCount = 10;
        await WriteAsync(repository, rtId, first, first.RtVersion, mode);

        second.GuardCount = 99;
        var ex = await Assert.ThrowsAsync<RtVersionConflictException>(() =>
            WriteAsync(repository, rtId, second, second.RtVersion, mode));

        Assert.Equal(second.RtVersion, ex.ExpectedRtVersion);
        Assert.Equal(rtId, ex.RtEntityId.RtId);
        var after = await ReadAsync(repository, rtId);
        Assert.Equal(10, after.GuardCount);
        Assert.Equal(second.RtVersion + 1, after.RtVersion);
    }

    [Theory]
    [InlineData(WriteMode.Replace)]
    [InlineData(WriteMode.Update)]
    public async Task ExpectedVersionOnMissingEntity_IsNotFound_NotAConflict(WriteMode mode)
    {
        var (repository, _) = await InsertCounterAsync(1);
        var missingId = OctoObjectId.GenerateNewId();
        var entity = await repository.CreateTransientRtEntityAsync<RtSeedGuardEntity>();
        entity.RtId = missingId;
        entity.GuardCount = 5;

        await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            WriteAsync(repository, missingId, entity, 0, mode));
    }

    [Fact]
    public async Task Replace_WithoutExpectedVersion_BehavesAsBefore()
    {
        var (repository, rtId) = await InsertCounterAsync(1);
        var read = await ReadAsync(repository, rtId);
        read.GuardCount = 7;

        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.ReplaceOneRtEntityByIdAsync(session, rtId, read);
            await session.CommitTransactionAsync();
        }

        var after = await ReadAsync(repository, rtId);
        Assert.Equal(7, after.GuardCount);
    }

    [Fact]
    public async Task BulkApplyChanges_OneStaleEntity_ThrowsAndTheTransactionWritesNothing()
    {
        var (repository, firstId) = await InsertCounterAsync(1, "first");
        var secondId = OctoObjectId.GenerateNewId();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var second = await repository.CreateTransientRtEntityAsync<RtSeedGuardEntity>();
            second.RtId = secondId;
            second.GuardCount = 1;
            second.GuardText = "second";
            await repository.InsertOneRtEntityAsync(session, second);
            await session.CommitTransactionAsync();
        }

        var firstRead = await ReadAsync(repository, firstId);
        var secondRead = await ReadAsync(repository, secondId);
        // a competing writer moves the second entity only (a replace leaves the written version in the entity)
        var staleSecondVersion = secondRead.RtVersion;
        secondRead.GuardCount = 50;
        await WriteAsync(repository, secondId, secondRead, secondRead.RtVersion, WriteMode.Replace);

        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            firstRead.GuardCount = 2;
            var staleSecond = await repository.CreateTransientRtEntityAsync<RtSeedGuardEntity>();
            staleSecond.GuardCount = 3;
            var updates = new List<IEntityUpdateInfo<RtEntity>>
            {
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkIds.RtCkSeedGuardEntityTypeId, firstId),
                    firstRead, firstRead.RtVersion),
                EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkIds.RtCkSeedGuardEntityTypeId, secondId),
                    staleSecond, staleSecondVersion)
            };

            await Assert.ThrowsAsync<RtVersionConflictException>(() =>
                repository.ApplyChangesAsync(session, updates, new OperationResult()));
            await session.AbortTransactionAsync();
        }

        // all-or-nothing: the first entity, which was fine, is unchanged as well
        Assert.Equal(1, (await ReadAsync(repository, firstId)).GuardCount);
        Assert.Equal(50, (await ReadAsync(repository, secondId)).GuardCount);
    }

    [Theory]
    [InlineData(WriteMode.Replace)]
    [InlineData(WriteMode.Update)]
    public async Task ParallelReadModifyWrite_WithGuardAndRetry_NeverLosesAnIncrement_Repeated50Times(WriteMode mode)
    {
        for (var run = 0; run < 50; run++)
        {
            var (repository, rtId) = await InsertCounterAsync(0);
            var initialVersion = (await ReadAsync(repository, rtId)).RtVersion;
            var bothHaveRead = new Barrier(2);

            // Correctness, not timing: a writer retries until its increment is written. Under load a writer may lose
            // the race more than once, so the cap is only a safety net against a livelock (each conflict means
            // another writer succeeded, so two writers need at most a few rounds).
            const int maxAttempts = 50;

            async Task<int> IncrementAsync()
            {
                var conflicts = 0;
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    var read = await ReadAsync(repository, rtId);
                    if (attempt == 0)
                    {
                        // force the race: both writers hold the same version before either one writes
                        bothHaveRead.SignalAndWait(TimeSpan.FromSeconds(120));
                    }

                    read.GuardCount = (read.GuardCount ?? 0) + 1;
                    try
                    {
                        await WriteAsync(repository, rtId, read, read.RtVersion, mode);
                        return conflicts;
                    }
                    catch (RtVersionConflictException)
                    {
                        conflicts++;
                    }
                }

                throw new InvalidOperationException($"no success after {maxAttempts} attempts");
            }

            var conflictCounts = await Task.WhenAll(Task.Run(IncrementAsync), Task.Run(IncrementAsync));

            var after = await ReadAsync(repository, rtId);
            Assert.Equal(2, after.GuardCount);
            Assert.Equal(initialVersion + 2, after.RtVersion);
            // Both writers read the same version before either wrote, so at least one conflict must have surfaced
            // (as RtVersionConflictException; any other exception fails the test). The exact number depends on timing.
            Assert.True(conflictCounts.Sum() >= 1, "the forced race must surface at least one version conflict");
        }
    }
}
