using System.Diagnostics;
using System.Globalization;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

using DeleteOptions = Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.LargeBinary;

/// <summary>
///     AB#6248 measurement (not a regression test): deletes N entities that each own a linked binary in ONE transaction
///     - the shape of the asset-repo recursive folder delete - and records the duration per phase. Runs only when
///     OCTO_MEASURE_DELETE_CAP is set; results are appended to the file named by OCTO_MEASURE_OUT.
///     OCTO_MEASURE_DELETE_CAP is a comma separated list of entry counts, e.g. "500,1000,2000,5000".
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class DeleteCapMeasurementTests(ImportTestCkModelFixture fixture)
{
    private const int FileBytes = 4096;
    private const int InsertBatch = 500;

    [Fact]
    public async Task MeasureSingleTransactionDelete()
    {
        var sizes = Environment.GetEnvironmentVariable("OCTO_MEASURE_DELETE_CAP");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(sizes), "OCTO_MEASURE_DELETE_CAP not set");
        var outFile = Environment.GetEnvironmentVariable("OCTO_MEASURE_OUT") ?? "delete-cap-measure.txt";
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var ownerIndex = await ApplyOwnerIndexSwitchAsync();

        foreach (var n in sizes!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(int.Parse))
        {
            var ids = await InsertAsync(tenantRepository, n);

            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
            string outcome = "ok";
            long applyMs = 0, commitMs = 0;
            var total = Stopwatch.StartNew();
            try
            {
                using var session = await tenantRepository.GetSessionAsync();
                session.StartTransaction();
                var sw = Stopwatch.StartNew();
                var result = new OperationResult();
                await tenantRepository.ApplyChangesAsync(session,
                    ids.Select(id => (IEntityUpdateInfo<RtEntity>)EntityUpdateInfo<RtEntity>.CreateDelete(id)).ToList(),
                    DeleteOptions.Erase, result);
                applyMs = sw.ElapsedMilliseconds;
                sw.Restart();
                await session.CommitTransactionAsync(); // includes the after-commit GridFS deletes (when deferred)
                commitMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                outcome = "FAILED " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            }

            total.Stop();
            var allocatedMb = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1024 / 1024;
            var workingSetMb = (Process.GetCurrentProcess().WorkingSet64 - workingSetBefore) / 1024 / 1024;

            var remaining = 0;
            foreach (var id in ids.Take(50))
            {
                if (await BinaryExistsAsync(tenantRepository, id.RtId)) remaining++;
            }

            await File.AppendAllTextAsync(outFile, string.Create(CultureInfo.InvariantCulture,
                $"entries={n} fileBytes={FileBytes} apply_ms={applyMs} commit_ms={commitMs} total_ms={total.ElapsedMilliseconds} " +
                $"allocated_mb={allocatedMb} workingset_delta_mb={workingSetMb} leftover_of_50_sampled={remaining} owner_index={ownerIndex} outcome={outcome}{Environment.NewLine}"),
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     AB#6538: OCTO_MEASURE_OWNER_INDEX=off drops the index on fs.files.metadata.rtEntityId, "on" (default) makes sure
    ///     it exists (tenant index update), so that the same machine can be measured before and after.
    /// </summary>
    private async Task<string> ApplyOwnerIndexSwitchAsync()
    {
        var mode = Environment.GetEnvironmentVariable("OCTO_MEASURE_OWNER_INDEX")?.Trim().ToLowerInvariant() ?? "on";
        var systemContext = fixture.GetSystemContext();
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var client = new MongoClient(new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        }.ToMongoUrl());
        var files = client.GetDatabase(config.SystemDatabaseName).GetCollection<BsonDocument>("fs.files");

        if (mode == "off")
        {
            try
            {
                await files.Indexes.DropOneAsync("metadata_rtEntityId_1", TestContext.Current.CancellationToken);
            }
            catch (MongoCommandException)
            {
                // not present
            }

            return "off";
        }

        using var adminSession = await systemContext.GetAdminSessionAsync();
        await systemContext.UpdateIndexesAsync(adminSession);
        return "on";
    }

    // Entities are inserted with BinaryId == null so that the engine uploads the bytes; the entity id doubles as the
    // key of the leftover check (BinaryExistsAsync downloads by the stored BinaryId, so keep a map).
    private static readonly Dictionary<OctoObjectId, OctoObjectId> BinaryOf = new();

    private static async Task<List<RtEntityId>> InsertAsync(ITenantRepository tenantRepository, int count)
    {
        var ids = new List<RtEntityId>(count);
        var payload = new byte[FileBytes];
        Random.Shared.NextBytes(payload);
        for (var offset = 0; offset < count; offset += InsertBatch)
        {
            var batch = Enumerable.Range(0, Math.Min(InsertBatch, count - offset)).Select(_ => new RtBinaryEntity
            {
                RtId = OctoObjectId.GenerateNewId(),
                DataCount = 1,
                Binary = new EntityBinaryInfo
                {
                    Filename = "m.bin",
                    ContentType = "application/octet-stream",
                    Stream = new MemoryStream(payload)
                }
            }).ToList();

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();
            await tenantRepository.ApplyChangesAsync(session,
                batch.Select(e => (IEntityUpdateInfo<RtEntity>)EntityUpdateInfo<RtEntity>.CreateInsert(e)).ToList(),
                new OperationResult());
            await session.CommitTransactionAsync();

            foreach (var e in batch)
            {
                ids.Add(e.ToRtEntityId());
                BinaryOf[e.RtId] = e.Binary.BinaryId!.Value;
            }
        }

        return ids;
    }

    private static async Task<bool> BinaryExistsAsync(ITenantRepository tenantRepository, OctoObjectId entityRtId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            using var handler = await tenantRepository.DownloadLargeBinaryAsync(session, BinaryOf[entityRtId]);
            return true;
        }
        catch (EntityNotFoundException)
        {
            return false;
        }
    }
}
