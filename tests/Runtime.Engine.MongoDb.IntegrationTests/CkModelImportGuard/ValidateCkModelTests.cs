using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;

/// <summary>
///     AB#8492: <see cref="ITenantContext.ValidateCkModelAsync" /> runs the import's checks against the models installed
///     in a real tenant and writes nothing - no CkModel row, no element rows, no tenant-update notification. A missing
///     dependency is an error message, not an exception.
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class ValidateCkModelTests(CkModelImportGuardFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");

    [Fact]
    public async Task AValidModel_HasNoError_AndIsNotImported()
    {
        await WithTenantAsync("valok", async (tenant, tenantId) =>
        {
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            var before = await SnapshotAsync(tenantId);

            var result = await tenant.ValidateCkModelAsync(await ClassicTestV1Async());

            Assert.False(result.HasErrors, string.Join("; ", result.Messages));
            Assert.False(result.HasFatalErrors);
            Assert.False(await tenant.IsCkModelExistingAsync(TestV1ModelId));
            Assert.Equal(before, await SnapshotAsync(tenantId));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
        });
    }

    [Fact]
    public async Task AMissingDependency_IsAnErrorMessage_NotAnException_AndNothingIsImported()
    {
        await WithTenantAsync("valdep", async (tenant, tenantId) =>
        {
            var model = await ClassicTestV1Async();
            var missing = new CkModelId("NotInstalledAnywhere-1.0.0");
            model.Dependencies = [.. model.Dependencies ?? [], missing];
            var before = await SnapshotAsync(tenantId);

            var result = await tenant.ValidateCkModelAsync(model);

            Assert.True(result.HasErrors || result.HasFatalErrors);
            // The resolver reports the unknown model as its own message and refuses the dependency set; both are errors.
            Assert.All(result.Messages.Where(m => m.MessageText.Contains("NotInstalledAnywhere")),
                m => Assert.True(m.MessageLevel is MessageLevel.Error or MessageLevel.FatalError, m.ToString()));
            var refusal = Assert.Single(result.Messages, m => m.MessageNumber == 8492);
            Assert.Equal("dependency-resolution", refusal.Parameters["reason"]);
            Assert.Contains("NotInstalledAnywhere", refusal.MessageText);
            Assert.False(await tenant.IsCkModelExistingAsync(TestV1ModelId));
            Assert.Equal(before, await SnapshotAsync(tenantId));
        });
    }

    [Fact]
    public async Task AnUnsupportedCkLanguage_IsMessage91_WithTheImportsText()
    {
        await WithTenantAsync("vallang", async (tenant, _) =>
        {
            var model = await ClassicTestV1Async();
            model.CkLanguage = CkModelPropertiesDto.MaxSupportedCkLanguage + 1;

            var result = await tenant.ValidateCkModelAsync(model);

            var message = Assert.Single(result.Messages);
            Assert.Equal(MessageLevel.Error, message.MessageLevel);
            Assert.Equal(91, message.MessageNumber);
            Assert.Contains("(CkLanguageNotSupported)", message.MessageText);
            Assert.Equal(TestV1ModelId.ToString(), message.Parameters["modelId"]);

            // The import refuses the same model with the same text (shared check, unchanged import behaviour).
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => tenant.ImportCkModelAsync(model));
            Assert.Contains(message.MessageText, ex.ToString());
        });
    }

    [Fact]
    public async Task AModelThatNeedsANewerEngine_IsMessage126()
    {
        await WithTenantAsync("valeng", async (tenant, _) =>
        {
            var model = await ClassicTestV1Async();
            model.MinEngineVersion = "9999.0.0";

            var result = await tenant.ValidateCkModelAsync(model);

            var message = Assert.Single(result.Messages);
            Assert.Equal(126, message.MessageNumber);
            Assert.Equal("9999.0.0", message.Parameters["minEngineVersion"]);
        });
    }

    private Task<CkCompiledModelRoot> ClassicTestV1Async() =>
        ClassicModelFactory.CreateAsync(fixture.GetService<ICatalogService>(), fixture.GetService<ICkJsonSerializer>(),
            TestV1ModelId, SystemCkIds.CkModelId);

    /// <summary>Ids of every CK document of the tenant, per collection: proves that validation wrote nothing.</summary>
    private async Task<string> SnapshotAsync(string tenantId)
    {
        var database = GetTenantDatabase(tenantId);
        var parts = new List<string>();
        foreach (var collection in new[]
                 {
                     "CkModel", "CkType", "CkAttribute", "CkRecord", "CkEnum", "CkAssociationRole",
                     "CkTypeAssociation", "CkTypeInheritance", "CkRecordInheritance"
                 })
        {
            var ids = await database.GetCollection<BsonDocument>(collection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Project(Builders<BsonDocument>.Projection.Include("_id").Include("modelState"))
                .ToListAsync(TestContext.Current.CancellationToken);
            parts.Add($"{collection}:{string.Join(",", ids.Select(d => d.ToJson()).Order(StringComparer.Ordinal))}");
        }

        return string.Join("|", parts);
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
}
