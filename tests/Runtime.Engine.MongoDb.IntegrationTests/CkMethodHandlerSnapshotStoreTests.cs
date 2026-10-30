using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for the persisted CK method handler registry snapshot (AB#5671).
/// </summary>
[Collection(SystemCollection.Name)]
public class CkMethodHandlerSnapshotStoreTests(SystemFixture fixture)
{
    private ICkMethodHandlerSnapshotStore Store => fixture.GetService<ICkMethodHandlerSnapshotStore>();

    private static CkMethodHandlerSnapshotRecord Row(string methodKey, string instance, DateTime lastSeen,
        string serviceName = "identity") => new()
    {
        MethodKey = methodKey,
        InstanceId = instance,
        MethodId = "System.Identity/User.ChangePassword-1",
        ServiceName = serviceName,
        Version = "1.2.3",
        ContractVersion = 1,
        LastSeenUtc = lastSeen,
        ExpiresUtc = lastSeen.AddSeconds(45),
        Tenants = ["*"],
        RetainUntilUtc = lastSeen.AddHours(24)
    };

    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public async Task Rows_are_written_and_loaded_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"k-{Guid.NewGuid():N}";
        var now = Now;
        var seen = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        Assert.True(await Store.UpsertAsync([Row(key, "a", seen), Row(key, "b", seen)], ct));

        var rows = (await Store.LoadAsync(ct)).Where(r => r.MethodKey == key).OrderBy(r => r.InstanceId).ToList();
        Assert.Equal(["a", "b"], rows.Select(r => r.InstanceId));
        Assert.Equal(seen, rows[0].LastSeenUtc);
        Assert.Equal(seen.AddSeconds(45), rows[0].ExpiresUtc);
        Assert.Equal(["*"], rows[0].Tenants);
        Assert.False(rows[0].Withdrawn);
    }

    [Fact]
    public async Task Upsert_updates_the_row_of_the_same_key_instead_of_adding_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"k-{Guid.NewGuid():N}";
        var seen = Now;

        await Store.UpsertAsync([Row(key, "a", seen)], ct);
        await Store.UpsertAsync([Row(key, "a", seen.AddSeconds(15))], ct);

        var row = Assert.Single(await Store.LoadAsync(ct), r => r.MethodKey == key);
        Assert.Equal(seen.AddSeconds(15).Ticks / TimeSpan.TicksPerMillisecond,
            row.LastSeenUtc.Ticks / TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public async Task An_older_view_never_replaces_a_newer_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"k-{Guid.NewGuid():N}";
        var seen = Now;

        await Store.UpsertAsync([Row(key, "a", seen)], ct);
        var withdrawn = Row(key, "a", seen.AddSeconds(5));
        withdrawn.Withdrawn = true;
        withdrawn.ExpiresUtc = withdrawn.LastSeenUtc;
        await Store.UpsertAsync([withdrawn], ct);

        // a replica that only saw the earlier heartbeat writes late: the withdrawal stays, and the older row
        // in the same batch as a newer unrelated row does not fail the batch
        var other = Row(key, "c", seen.AddSeconds(6));
        Assert.True(await Store.UpsertAsync([Row(key, "a", seen.AddSeconds(1)), other], ct));

        var rows = (await Store.LoadAsync(ct)).Where(r => r.MethodKey == key).OrderBy(r => r.InstanceId).ToList();
        Assert.Equal(["a", "c"], rows.Select(r => r.InstanceId));
        Assert.True(rows[0].Withdrawn);
    }

    [Fact]
    public async Task The_stored_document_has_service_data_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"k-{Guid.NewGuid():N}";
        await Store.UpsertAsync([Row(key, "a", Now)], ct);

        var systemContext = fixture.GetService<ISystemContext>();
        var access = fixture.GetService<IAdminRepositoryAccess>();
        var repository = (MongoRepository)access.GetRepositoryClient(systemContext.DatabaseName)
            .GetRepository(systemContext.DatabaseName);
        var document = await repository.Database.GetCollection<BsonDocument>("ck_method_handler_snapshot")
            .Find(Builders<BsonDocument>.Filter.Eq("methodKey", key)).FirstAsync(ct);

        // an allow-list: a new field has to be added here on purpose, after asking whether it is service data
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "_id", "methodKey", "instanceId", "methodId", "serviceName", "version", "contractVersion", "lastSeenUtc",
            "expiresUtc", "withdrawn", "withdrawnAtSource", "noQueueBound", "tenants", "retainUntilUtc"
        };
        Assert.All(document.Names, n => Assert.Contains(n, allowed));
    }

    [Fact]
    public async Task Rows_have_a_ttl_index_and_a_unique_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store.UpsertAsync([Row($"k-{Guid.NewGuid():N}", "a", Now)], ct);

        var systemContext = fixture.GetService<ISystemContext>();
        var access = fixture.GetService<IAdminRepositoryAccess>();
        var repository = (MongoRepository)access.GetRepositoryClient(systemContext.DatabaseName)
            .GetRepository(systemContext.DatabaseName);
        var indexes = await (await repository.Database.GetCollection<BsonDocument>("ck_method_handler_snapshot")
            .Indexes.ListAsync(ct)).ToListAsync(ct);

        Assert.Contains(indexes, i => i["name"] == "ck_method_handler_snapshot_ttl" && i["expireAfterSeconds"] == 0);
        Assert.Contains(indexes, i => i["name"] == "ck_method_handler_snapshot_key_unique" && i["unique"] == true);
    }
}
