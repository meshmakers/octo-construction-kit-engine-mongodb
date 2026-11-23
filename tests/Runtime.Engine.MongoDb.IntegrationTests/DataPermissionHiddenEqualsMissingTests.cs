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
///     collection, abstract base type), while the caller's own visible record still answers the validation error.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class DataPermissionHiddenEqualsMissingTests(DataPermissionTestFixture fixture)
{
    public enum PolicyConfiguration
    {
        OwnedOnlyForEveryAction,
        WriteAndDeleteAllReadOwnedOnly,
        WriteAndDeleteAllWithoutRead
    }

    private const string NotFoundMessageShape = "is not existing for document type RtEntity";
    private const string ValidationErrorMessageShape = "Operation result contains errors.";

    private static readonly RtSecurityContext OwnedOnlyEmployee = RtSecurityContext.ForUser("user-1", ["TestEmployee"]);
    private static readonly RtSecurityContext OtherEmployee = RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    private static readonly RtCkId<CkTypeId> ContinentTypeId = TestCkIds.RtCkContinentTypeId;
    private static readonly RtCkId<CkTypeId> CountryTypeId = TestCkIds.RtCkCountryTypeId;
    private static readonly RtCkId<CkTypeId> LocationTypeId = TestCkIds.RtCkLocationTypeId;

    private sealed record Answer(string ExceptionType, string Message);

    private static RtDataPolicyTable PolicyTable(PolicyConfiguration configuration)
    {
        var continentTypes = new HashSet<string> { ContinentTypeId.SemanticVersionedFullName };
        var employeeRoles = new HashSet<string> { "TestEmployee" };
        var managementRule = new RtDataPolicyRule("test.continents.all", continentTypes,
            [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
            OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" });

        RtDataPolicyRule[] employeeRules = configuration switch
        {
            PolicyConfiguration.OwnedOnlyForEveryAction =>
            [
                new RtDataPolicyRule("test.continents", continentTypes,
                    [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
                    OwnedOnly: true, AuditOnly: false, employeeRoles)
            ],
            PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly =>
            [
                new RtDataPolicyRule("test.continents.read", continentTypes, [RtDataAction.Read],
                    OwnedOnly: true, AuditOnly: false, employeeRoles),
                new RtDataPolicyRule("test.continents.write", continentTypes,
                    [RtDataAction.Write, RtDataAction.Delete],
                    OwnedOnly: false, AuditOnly: false, employeeRoles)
            ],
            PolicyConfiguration.WriteAndDeleteAllWithoutRead =>
            [
                new RtDataPolicyRule("test.continents.write", continentTypes,
                    [RtDataAction.Write, RtDataAction.Delete],
                    OwnedOnly: false, AuditOnly: false, employeeRoles)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(configuration), configuration, "Unknown configuration")
        };

        return new RtDataPolicyTable([.. employeeRules, managementRule]);
    }

    private async Task<(ITenantRepository Repository, OctoObjectId ForeignRtId)> SeedForeignContinentAsync()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var foreignRtId = await InsertContinentAsync(tenantRepository, OtherEmployee, "Foreign");
        return (tenantRepository, foreignRtId);
    }

    private static async Task<OctoObjectId> InsertContinentAsync(ITenantRepository tenantRepository,
        RtSecurityContext owner, string name)
    {
        using var session = await tenantRepository.GetSessionAsync(owner);
        session.StartTransaction();
        var continent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = name;
        await tenantRepository.InsertOneRtEntityAsync(session, continent);
        await session.CommitTransactionAsync();

        Assert.Equal(name, await StoredContinentNameAsync(tenantRepository, continent.RtId));
        return continent.RtId;
    }

    private static async Task<string?> StoredContinentNameAsync(ITenantRepository tenantRepository,
        OctoObjectId rtId)
    {
        using var systemSession = await tenantRepository.GetSessionAsync();
        var storedContinent = await tenantRepository.GetRtEntityByRtIdAsync(systemSession,
            new RtEntityId(ContinentTypeId, rtId));
        Assert.NotNull(storedContinent);
        return storedContinent.GetAttributeStringValueOrDefault("Name");
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
        RtSecurityContext securityContext, IEntityUpdateInfo<RtEntity> change, OctoObjectId rtId) =>
        AnswerOfWriteAsync(tenantRepository, securityContext,
            session => tenantRepository.ApplyChangesAsync(session, [change], new OperationResult()), rtId);

    private static async Task<Answer> AnswerOfWriteAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, Func<IOctoSession, Task> write, OctoObjectId rtId)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var exception = await Record.ExceptionAsync(() => write(session));
        await session.AbortTransactionAsync();
        return ToAnswer(exception, rtId);
    }

    private static async Task<Answer?> AnswerOfChangeWithoutTransactionAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, IEntityUpdateInfo<RtEntity> change, OctoObjectId rtId)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        var exception = await Record.ExceptionAsync(() =>
            tenantRepository.ApplyChangesAsync(session, [change], new OperationResult()));
        return ToAnswerOrNull(exception, rtId);
    }

    private static Answer ToAnswer(Exception? exception, OctoObjectId rtId)
    {
        Assert.NotNull(exception);
        return ToAnswerOrNull(exception, rtId)!;
    }

    private static Answer? ToAnswerOrNull(Exception? exception, OctoObjectId rtId) =>
        exception == null
            ? null
            : new Answer(exception.GetType().Name, exception.Message.Replace(rtId.ToString(), "<rtId>"));

    private async Task WithPoliciesAsync(PolicyConfiguration configuration, Func<Task> assertions)
    {
        fixture.Resolver.Table = PolicyTable(configuration);
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
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "update", true)]
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "update", false)]
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "replace", true)]
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "replace", false)]
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "delete", true)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "update", true)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "update", false)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "replace", true)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "replace", false)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "delete", true)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllWithoutRead, "update", true)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllWithoutRead, "update", false)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllWithoutRead, "replace", true)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllWithoutRead, "replace", false)]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllWithoutRead, "delete", true)]
    public async Task HiddenRecord_AnswersExactlyLikeMissingRecord(PolicyConfiguration configuration,
        string operation, bool validPayload)
    {
        var (repository, foreignRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        await WithPoliciesAsync(configuration, async () =>
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
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "update")]
    [InlineData(PolicyConfiguration.OwnedOnlyForEveryAction, "replace")]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "update")]
    [InlineData(PolicyConfiguration.WriteAndDeleteAllReadOwnedOnly, "replace")]
    public async Task OwnVisibleRecord_WithInvalidPayload_AnswersTheValidationError(
        PolicyConfiguration configuration, string operation)
    {
        var (repository, _) = await SeedForeignContinentAsync();
        var ownRtId = await InsertContinentAsync(repository, OwnedOnlyEmployee, "Own");

        await WithPoliciesAsync(configuration, async () =>
        {
            var ownAnswer = await AnswerOfChangeAsync(repository, OwnedOnlyEmployee,
                Change(operation, ContinentTypeId, ownRtId, validPayload: false), ownRtId);

            Assert.Equal(nameof(RuntimeRepositoryException), ownAnswer.ExceptionType);
            Assert.StartsWith(ValidationErrorMessageShape, ownAnswer.Message);
            Assert.DoesNotContain(NotFoundMessageShape, ownAnswer.Message);
        });

        Assert.Equal("Own", await StoredContinentNameAsync(repository, ownRtId));
    }

    [Theory]
    [InlineData("update", true)]
    [InlineData("update", false)]
    [InlineData("replace", true)]
    [InlineData("replace", false)]
    public async Task HiddenRecordAddressedAsSiblingType_AnswersExactlyLikeMissingRecordAndIsNotWritten(
        string operation, bool validPayload)
    {
        var (repository, foreignRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        await WithPoliciesAsync(PolicyConfiguration.OwnedOnlyForEveryAction, async () =>
        {
            var siblingAnswer = await AnswerOfChangeWithoutTransactionAsync(repository, OwnedOnlyEmployee,
                Change(operation, CountryTypeId, foreignRtId, validPayload), foreignRtId);
            var missingAnswer = await AnswerOfChangeWithoutTransactionAsync(repository, OwnedOnlyEmployee,
                Change(operation, CountryTypeId, missingRtId, validPayload), missingRtId);

            Assert.Equal("Foreign", await StoredContinentNameAsync(repository, foreignRtId));
            Assert.NotNull(missingAnswer);
            Assert.Equal(missingAnswer, siblingAnswer);
            if (operation == "update" && validPayload)
            {
                AssertNotFound(missingAnswer);
            }
        });
    }

    [Fact]
    public async Task HiddenRecordDeletedByAbstractType_AnswersExactlyLikeMissingRecord()
    {
        var (repository, foreignRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        await WithPoliciesAsync(PolicyConfiguration.OwnedOnlyForEveryAction, async () =>
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
