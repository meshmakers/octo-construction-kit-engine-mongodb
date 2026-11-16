using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A tenant dump never carries platform-only data: the secret defaults the communication controller projects into
///     a tenant stay out of the archive, everything else of the tenant is in it.
/// </summary>
[Collection(SystemCollection.Name)]
public class TenantDumpPlatformOnlyExclusionTests(SystemFixture systemFixture)
{
    private const string SecretDefaultCollectionName = "RtEntity_SystemCommunicationSecretDefault";
    private const string OrdinaryCollectionName = "RtEntity_SystemCommunicationAdapter";

    [Fact]
    public void PlatformOnlyCollections_NameTheSecretDefaultCollectionRoot()
    {
        Assert.Contains(SecretDefaultCollectionName, MongoDumpOptions.PlatformOnlyTenantCollections);
    }

    [Fact]
    public async Task BackupTenant_LeavesSecretDefaultsOutOfTheArchive_AndKeepsTheRest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var repositoryOps = systemFixture.Provider!.GetRequiredService<IRepositoryOpsService>();
        var tenantId = $"dumpexcl_{Guid.NewGuid():N}";
        var databaseName = tenantId.ToLowerInvariant();
        var archivePath = Path.Combine(Path.GetTempPath(), $"dumpexcl_{Guid.NewGuid():N}.archive.gz");

        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, databaseName, tenantId);
                await session.CommitTransactionAsync();
            }

            var database = GetDatabase(databaseName);
            await database.GetCollection<BsonDocument>(SecretDefaultCollectionName)
                .InsertOneAsync(new BsonDocument { { "_id", 1 }, { "Name", "platform-default" } },
                    cancellationToken: cancellationToken);
            await database.GetCollection<BsonDocument>(OrdinaryCollectionName)
                .InsertOneAsync(new BsonDocument { { "_id", 1 }, { "Name", "ordinary" } },
                    cancellationToken: cancellationToken);

            var backupResult = await systemContext.BackupTenantAsync(tenantId, archivePath);
            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");

            await database.DropCollectionAsync(SecretDefaultCollectionName, cancellationToken);
            await database.DropCollectionAsync(OrdinaryCollectionName, cancellationToken);

            var restoreResult = await repositoryOps.ExecuteMongoRestoreAsync(
                new MongoRestoreOptions { Database = databaseName, Archive = archivePath, Gzip = true, Drop = true },
                TimeSpan.FromMinutes(5), cancellationToken);
            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            var restoredCollectionNames = await (await database.ListCollectionNamesAsync(
                cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
            Assert.DoesNotContain(SecretDefaultCollectionName, restoredCollectionNames);
            Assert.Contains(OrdinaryCollectionName, restoredCollectionNames);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var config = systemFixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName);
    }
}
