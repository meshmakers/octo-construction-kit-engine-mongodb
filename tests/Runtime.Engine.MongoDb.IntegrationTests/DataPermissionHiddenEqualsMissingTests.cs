using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
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
///     On the persisted path an update, replace or delete of a record the caller may not see answers exactly like the
///     same call with a missing rtId, whatever the payload; a missing record answers not found before its payload is
///     validated also for system sessions, while an existing record keeps its validation error.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class DataPermissionHiddenEqualsMissingTests(DataPermissionTestFixture fixture)
{
    private static readonly RtSecurityContext OwnedOnlyEmployee =
        RtSecurityContext.ForUser("user-1", ["TestEmployee"]);

    private static readonly RtSecurityContext OtherEmployee =
        RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    private static readonly RtCkId<CkTypeId> ContinentTypeId = TestCkIds.RtCkContinentTypeId;

    private sealed record Answer(string ExceptionType, string Message);

    private static RtDataPolicyTable OwnedOnlyTable() =>
        new(
        [
            new RtDataPolicyRule("test.continents", new HashSet<string> { ContinentTypeId.SemanticVersionedFullName },
                [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
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
        return (tenantRepository, continent.RtId);
    }

    private static IEntityUpdateInfo<RtEntity> Change(string operation, OctoObjectId rtId, bool validPayload)
    {
        var entityId = new RtEntityId(ContinentTypeId, rtId);
        var payload = new RtEntity(ContinentTypeId, rtId,
            new Dictionary<string, object?> { { "Name", validPayload ? "Changed" : null } });
        return operation switch
        {
            "update" => EntityUpdateInfo<RtEntity>.CreateUpdate(entityId, payload),
            "replace" => EntityUpdateInfo<RtEntity>.CreateReplace(entityId, payload),
            "delete" => EntityUpdateInfo<RtEntity>.CreateDelete(entityId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation")
        };
    }

    private static Task<Answer> AnswerOfAsync(ITenantRepository tenantRepository,
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SystemSession_AssociationFromMissingOrigin_AnswersNotFound(bool validRole)
    {
        var (repository, continentRtId) = await SeedForeignContinentAsync();
        var missingCountryRtId = OctoObjectId.GenerateNewId();
        var roleId = validRole ? SystemCkIds.RtCkParentChildRoleId : new RtCkId<CkAssociationRoleId>("Test/Undefined");
        var association = AssociationUpdateInfo.CreateInsert(
            new RtEntityId(TestCkIds.RtCkCountryTypeId, missingCountryRtId),
            new RtEntityId(ContinentTypeId, continentRtId), roleId);

        var answer = await AnswerOfWriteAsync(repository, null,
            session => repository.ApplyChangesAsync(session, [], [association], new OperationResult()),
            missingCountryRtId);

        Assert.Equal(nameof(EntityNotFoundException), answer.ExceptionType);
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
        fixture.Resolver.Table = OwnedOnlyTable();
        try
        {
            var hiddenAnswer = await AnswerOfAsync(repository, OwnedOnlyEmployee,
                Change(operation, foreignRtId, validPayload), foreignRtId);
            var missingAnswer = await AnswerOfAsync(repository, OwnedOnlyEmployee,
                Change(operation, missingRtId, validPayload), missingRtId);

            Assert.Equal(nameof(EntityNotFoundException), hiddenAnswer.ExceptionType);
            Assert.Equal(hiddenAnswer, missingAnswer);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Theory]
    [InlineData("update")]
    [InlineData("replace")]
    public async Task SystemSession_InvalidPayload_MissingRecordIsNotFound_ExistingRecordKeepsTheValidationError(
        string operation)
    {
        var (repository, existingRtId) = await SeedForeignContinentAsync();
        var missingRtId = OctoObjectId.GenerateNewId();

        var missingAnswer = await AnswerOfAsync(repository, null, Change(operation, missingRtId, false), missingRtId);
        var existingAnswer = await AnswerOfAsync(repository, null, Change(operation, existingRtId, false),
            existingRtId);
        var missingWithValidPayload = await AnswerOfAsync(repository, null, Change(operation, missingRtId, true),
            missingRtId);

        Assert.Equal(missingWithValidPayload, missingAnswer);
        Assert.Equal(nameof(EntityNotFoundException), missingAnswer.ExceptionType);
        Assert.Equal(nameof(RuntimeRepositoryException), existingAnswer.ExceptionType);
    }
}
