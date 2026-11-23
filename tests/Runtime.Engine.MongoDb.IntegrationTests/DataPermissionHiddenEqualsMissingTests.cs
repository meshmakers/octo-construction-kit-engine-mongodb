using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
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
///     On the persisted path a record a caller with restricted read may not see answers exactly like a missing rtId,
///     whatever the payload and however the record is addressed (stored type, sibling type of the shared Location
///     collection, abstract base type).
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class DataPermissionHiddenEqualsMissingTests(DataPermissionTestFixture fixture)
{
    private const string NotFoundMessageShape = "is not existing for document type RtEntity";

    private static readonly RtSecurityContext OwnedOnlyEmployee = RtSecurityContext.ForUser("user-1", ["TestEmployee"]);
    private static readonly RtSecurityContext OtherEmployee = RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    private static readonly RtCkId<CkTypeId> ContinentTypeId = TestCkIds.RtCkContinentTypeId;
    private static readonly RtCkId<CkTypeId> CountryTypeId = TestCkIds.RtCkCountryTypeId;
    private static readonly RtCkId<CkTypeId> LocationTypeId = TestCkIds.RtCkLocationTypeId;

    private sealed record Answer(string ExceptionType, string Message);

    private static RtDataPolicyTable PolicyTable() =>
        new(
        [
            new RtDataPolicyRule("test.continents", new HashSet<string> { ContinentTypeId.SemanticVersionedFullName },
                [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" }),
            new RtDataPolicyRule("test.continents.all", new HashSet<string> { ContinentTypeId.SemanticVersionedFullName },
                [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
                OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" })
        ]);

    private async Task<(ITenantRepository Repository, OctoObjectId ForeignRtId)> SeedForeignContinentAsync()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync(OtherEmployee);
        session.StartTransaction();
        var continent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = "Foreign";
        await tenantRepository.InsertOneRtEntityAsync(session, continent);
        await session.CommitTransactionAsync();

        using var systemSession = await tenantRepository.GetSessionAsync();
        var stored = await tenantRepository.GetRtEntityByRtIdAsync(systemSession,
            new RtEntityId(ContinentTypeId, continent.RtId));
        Assert.NotNull(stored);
        return (tenantRepository, continent.RtId);
    }

    private static IEntityUpdateInfo<RtEntity> Change(string operation, RtCkId<CkTypeId> addressedTypeId,
        OctoObjectId rtId, bool validPayload)
    {
        var entityId = new RtEntityId(addressedTypeId, rtId);
        var payload = new RtEntity(addressedTypeId, rtId,
            new Dictionary<string, object?> { { "Name", validPayload ? "Changed" : null } });
        return operation switch
        {
            "update" => EntityUpdateInfo<RtEntity>.CreateUpdate(entityId, payload),
            "replace" => EntityUpdateInfo<RtEntity>.CreateReplace(entityId, payload),
            "delete" => EntityUpdateInfo<RtEntity>.CreateDelete(entityId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation")
        };
    }

    private static Task<Answer> AnswerOfChangeAsync(ITenantRepository tenantRepository,
        RtSecurityContext? securityContext, IEntityUpdateInfo<RtEntity> change, OctoObjectId rtId) =>
        AnswerOfWriteAsync(tenantRepository, securityContext,
            session => tenantRepository.ApplyChangesAsync(session, [change], new OperationResult()), rtId);

    private static async Task<Answer> AnswerOfWriteAsync(ITenantRepository tenantRepository,
        RtSecurityContext? securityContext, Func<IOctoSession, Task> write, OctoObjectId rtId)
    {
        using var session = securityContext == null
            ? await tenantRepository.GetSessionAsync()
            : await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var exception = await Record.ExceptionAsync(() => write(session));
        await session.AbortTransactionAsync();
        Assert.NotNull(exception);
        return new Answer(exception.GetType().Name, exception.Message.Replace(rtId.ToString(), "<rtId>"));
    }

    private async Task WithPoliciesAsync(Func<Task> assertions)
    {
        fixture.Resolver.Table = PolicyTable();
        try
        {
            await assertions();
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private static void AssertNotFound(Answer answer)
    {
        Assert.Equal(nameof(EntityNotFoundException), answer.ExceptionType);
        Assert.Contains(NotFoundMessageShape, answer.Message);
    }

    [Theory]
    [InlineData("update", true)]
    [InlineData("update", false)]
    [InlineData("replace", true)]
    [InlineData("replace", false)]
    [InlineData("delete", true)]
    public async Task HiddenRecord_AnswersExactlyLikeMissingRecord(string operation, bool validPayload)
    {
        var (repository, foreignRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        await WithPoliciesAsync(async () =>
        {
            var hiddenAnswer = await AnswerOfChangeAsync(repository, OwnedOnlyEmployee,
                Change(operation, ContinentTypeId, foreignRtId, validPayload), foreignRtId);
            var missingAnswer = await AnswerOfChangeAsync(repository, OwnedOnlyEmployee,
                Change(operation, ContinentTypeId, missingRtId, validPayload), missingRtId);

            Assert.Equal(nameof(EntityNotFoundException), hiddenAnswer.ExceptionType);
            Assert.Equal(hiddenAnswer, missingAnswer);
        });
    }

    [Theory]
    [InlineData("update", true)]
    [InlineData("update", false)]
    [InlineData("replace", true)]
    [InlineData("replace", false)]
    public async Task HiddenRecordAddressedAsSiblingType_AnswersExactlyLikeMissingRecord(string operation,
        bool validPayload)
    {
        var (repository, foreignRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        await WithPoliciesAsync(async () =>
        {
            var siblingAnswer = await AnswerOfChangeAsync(repository, OwnedOnlyEmployee,
                Change(operation, CountryTypeId, foreignRtId, validPayload), foreignRtId);
            var missingAnswer = await AnswerOfChangeAsync(repository, OwnedOnlyEmployee,
                Change(operation, CountryTypeId, missingRtId, validPayload), missingRtId);

            Assert.Equal(missingAnswer, siblingAnswer);
            if (operation == "update" && validPayload)
            {
                AssertNotFound(siblingAnswer);
            }
        });

        using var systemSession = await repository.GetSessionAsync();
        var storedContinent = await repository.GetRtEntityByRtIdAsync(systemSession,
            new RtEntityId(ContinentTypeId, foreignRtId));
        Assert.NotNull(storedContinent);
        Assert.Equal("Foreign", storedContinent.GetAttributeStringValueOrDefault("Name"));
    }

    [Fact]
    public async Task HiddenRecordDeletedByAbstractType_AnswersExactlyLikeMissingRecord()
    {
        var (repository, foreignRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        await WithPoliciesAsync(async () =>
        {
            var hiddenAnswer = await AnswerOfWriteAsync(repository, OwnedOnlyEmployee, session =>
                repository.DeleteOneRtEntityByRtIdAsync(session, LocationTypeId, foreignRtId, DeleteOptions.Erase),
                foreignRtId);
            var missingAnswer = await AnswerOfWriteAsync(repository, OwnedOnlyEmployee, session =>
                repository.DeleteOneRtEntityByRtIdAsync(session, LocationTypeId, missingRtId, DeleteOptions.Erase),
                missingRtId);

            Assert.Equal(nameof(EntityNotFoundException), hiddenAnswer.ExceptionType);
            Assert.Equal(hiddenAnswer, missingAnswer);
        });
    }
}
