using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>Creates tenants, dumps them without any exclusion and reads their databases directly.</summary>
internal sealed class RestoreArchiveSupport(SystemFixture systemFixture)
{
    public async Task CreateTenantAsync(string tenantId)
    {
        var systemContext = systemFixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    public async Task DumpWithoutAnyExclusionAsync(string tenantId, string archivePath,
        CancellationToken cancellationToken)
    {
        var repositoryOps = systemFixture.Provider!.GetRequiredService<IRepositoryOpsService>();
        var dumpResult = await repositoryOps.ExecuteMongoDumpAsync(
            MongoDumpOptions.ForArchive(tenantId, archivePath), TimeSpan.FromMinutes(5), cancellationToken);
        Assert.True(dumpResult.Success, $"Dump should succeed. Error: {dumpResult.Error}");
    }

    public IMongoDatabase GetDatabase(string databaseName)
    {
        var configuration = systemFixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(configuration.DatabaseHost),
            Username = configuration.AdminUser,
            Password = configuration.AdminUserPassword,
            AuthenticationSource = configuration.AuthenticationDatabaseName,
            DatabaseName = configuration.AuthenticationDatabaseName,
            DirectConnection = configuration.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName);
    }

    public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken cancellationToken)
    {
        var databaseNames = await (await GetDatabase(databaseName).Client.ListDatabaseNamesAsync(cancellationToken))
            .ToListAsync(cancellationToken);
        return databaseNames.Contains(databaseName);
    }

    public static async Task<IReadOnlyList<string>> CollectionNamesAsync(IMongoDatabase database,
        CancellationToken cancellationToken) =>
        await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
}
