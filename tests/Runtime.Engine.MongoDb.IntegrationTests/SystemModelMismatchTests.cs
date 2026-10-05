using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Pins AB#5492: a process whose compiled-in System CK model version is not the one installed in
///     the system database must say so, instead of hiding behind "database does not exist, is not
///     accessible or the system model is missing". That generic text cost a day of diagnosis when
///     the finAPI adapter (built against System-2.2.2) kept running after the platform had moved the
///     system database to System-2.3.0 (prod-1, 1.-5.10.2026).
/// </summary>
[Collection(SystemModelMismatchCollection.Name)]
public class SystemModelMismatchTests(SystemModelMismatchFixture fixture)
{
    private const string ForeignSystemModelId = "System-99.0.0";

    [Fact]
    public async Task TryFindTenantContext_WhenInstalledSystemModelHasAnotherVersion_NamesBothSides()
    {
        var systemContext = fixture.GetSystemContext();
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
        var ckModels = new MongoClient(urlBuilder.ToMongoUrl())
            .GetDatabase(config.SystemDatabaseName)
            .GetCollection<BsonDocument>("CkModel");

        Assert.True(await systemContext.IsSystemTenantExistingAsync());

        // Reproduce the production state from the adapter's point of view: the database is intact,
        // but its System CK model is a version other than the one compiled into this process. The
        // model id is the document's _id (stored as "Name-Version"), so the version move is a
        // re-keyed copy of the installed document.
        var installed = await ckModels
            .Find(Builders<BsonDocument>.Filter.Eq("_id", SystemCkIds.CkModelId.ToString()))
            .SingleAsync(TestContext.Current.CancellationToken);
        var foreign = (BsonDocument)installed.DeepClone();
        foreign["_id"] = ForeignSystemModelId;
        await ckModels.InsertOneAsync(foreign, cancellationToken: TestContext.Current.CancellationToken);
        await ckModels.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", SystemCkIds.CkModelId.ToString()),
            TestContext.Current.CancellationToken);

        Assert.False(await systemContext.IsSystemTenantExistingAsync());

        // Same exception type as before — callers that map TenantException stay unaffected.
        var exception = await Assert.ThrowsAsync<TenantException>(
            async () => await systemContext.TryFindTenantContextAsync(config.SystemTenantId));

        Assert.Contains($"System CK model '{SystemCkIds.CkModelId}' (compiled into this process) is not installed",
            exception.Message);
        Assert.Contains($"installed System models: {ForeignSystemModelId}", exception.Message);
        Assert.Contains("must be rebuilt/re-released", exception.Message);
        Assert.DoesNotContain("does not exist, is not accessible", exception.Message);

        // A genuinely missing model keeps the generic message: nothing to name on the installed side.
        await ckModels.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", ForeignSystemModelId),
            TestContext.Current.CancellationToken);

        exception = await Assert.ThrowsAsync<TenantException>(
            async () => await systemContext.TryFindTenantContextAsync(config.SystemTenantId));

        Assert.Contains("does not exist, is not accessible or the system model is missing", exception.Message);
    }
}
