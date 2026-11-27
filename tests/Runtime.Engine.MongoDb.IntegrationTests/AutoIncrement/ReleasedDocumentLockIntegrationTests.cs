using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using DeleteOptions = Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     The lock of released documents against a real MongoDB, in throwaway child tenants with the model of
///     <see cref="AutoIncrementReleaseTests" /> (Invoice: <c>Title</c>, enum <c>DocumentState</c>, <c>Number</c> from the
///     range "InvoiceNumber" with <c>ReleasedValues = [Released, Cancelled]</c>). Every public write path of the tenant
///     repository and of the RT import is tried on a released document: the write is refused with engine message 168
///     (<see cref="ReleasedDocumentLockedException" />), the stored document is byte for byte the same raw BSON document,
///     and a <c>Denied</c> audit entry is written outside the aborted transaction. Drafts are written as before; an import
///     of the stored content is a no-op; a tenant dump restored with mongorestore and attached brings documents, the
///     counter and the audit entries back, and numbering goes on from the restored counter.
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class ReleasedDocumentLockIntegrationTests(CkModelImportGuardFixture fixture)
{
    private const string StateAttribute = "DocumentState";
    private const int Draft = 0;
    private const int Released = 1;

    private static readonly RtCkId<CkTypeId> InvoiceTypeId = new("ReleaseTest/Invoice");
    private static readonly RtCkId<CkTypeId> AutoIncrementTypeId = new("System/AutoIncrement");
    private static readonly RtCkId<CkTypeId> CreditNoteTypeId = new("ReleaseTest/CreditNote");
    private static readonly RtCkId<CkAssociationRoleId> CancelsRoleId = new("System/Cancels");

    public static TheoryData<string> RepositoryWritePaths =>
    [
        "ApplyChanges update", "UpdateOneRtEntityById", "UpdateOneRtEntity by filter", "UpdateManyRtEntity by filter",
        "ReplaceOneRtEntityById", "ReplaceOneRtEntity by filter", "DeleteOneRtEntityByRtId archive",
        "DeleteOneRtEntityByRtId erase", "DeleteOneRtEntity by filter", "DeleteManyRtEntities by filter",
        "InsertOneRtEntity with the stored id"
    ];

    /// <summary>
    ///     Abuse cases 1, 2, 5 on every write path of the repository (by id, by filter, ApplyChanges, insert): refused
    ///     with message 168, the raw document unchanged, one Denied entry naming what the write would have changed.
    /// </summary>
    [Theory]
    [MemberData(nameof(RepositoryWritePaths))]
    public async Task EveryRepositoryWritePath_IsRefusedForAReleasedDocument(string path)
    {
        await WithTenantAsync("rellock", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var released = await InsertInvoiceAsync(repository, Released);
            var before = await RawAsync(database, released.RtId);

            var exception = await Record.ExceptionAsync(() => WriteAsync(repository, path, released.RtId));

            var refused = Assert.IsType<ReleasedDocumentLockedException>(exception);
            Assert.Equal(168, Assert.Single(refused.OperationResult.Messages).MessageNumber);
            Assert.Equal(before, await RawAsync(database, released.RtId));
            var denied = Assert.Single(await DeniedEntriesAsync(database, released.RtId))["attributes"].AsBsonDocument;
            Assert.Equal(2, denied["action"].ToInt32());
            Assert.StartsWith("168:", denied["message"].AsString);
        });
    }

    /// <summary>A draft goes through the same paths as before the lock (regression).</summary>
    [Theory]
    [MemberData(nameof(RepositoryWritePaths))]
    public async Task EveryRepositoryWritePath_WritesADraftAsBefore(string path)
    {
        if (path.StartsWith("InsertOne", StringComparison.Ordinal))
        {
            return; // an insert with a stored id is a duplicate for a draft too; not a lock case
        }

        await WithTenantAsync("reldraft", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var draft = await InsertInvoiceAsync(repository, Draft);
            var before = await RawAsync(database, draft.RtId);

            await WriteAsync(repository, path, draft.RtId);

            Assert.NotEqual(before, await TryRawAsync(database, draft.RtId));
            Assert.Empty(await DeniedEntriesAsync(database, draft.RtId));
        });
    }

    /// <summary>Changeable attributes are written; the number and the rest stay as they are.</summary>
    [Fact]
    public async Task ChangeableAttribute_IsWritten_AndNothingElseChanges()
    {
        await WithTenantAsync("relchg", async (repository, database) =>
        {
            await InsertRangeAsync(repository, changeable: ["Title"]);
            var released = await InsertInvoiceAsync(repository, Released);
            var before = (await RawAsync(database, released.RtId))["attributes"].AsBsonDocument;

            var paid = Partial(released.RtId, ("Title", "paid"));
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(paid.ToRtEntityId(), paid));

            var after = (await RawAsync(database, released.RtId))["attributes"].AsBsonDocument;
            Assert.Equal("paid", after["title"].AsString);
            Assert.Equal(before["number"], after["number"]);
            Assert.Equal(before["documentState"], after["documentState"]);
            Assert.Empty(await DeniedEntriesAsync(database, released.RtId));
        });
    }

    /// <summary>
    ///     Abuse case 9 and the import criterion: an <c>importrt</c> Upsert that changes a released document is refused
    ///     with the same error as the API (not wrapped), the document is unchanged and a Denied entry names the attribute.
    /// </summary>
    [Fact]
    public async Task ImportUpsert_ThatChangesAReleasedDocument_IsRefused_WithADeniedEntry()
    {
        await WithTenantAsync("relimp", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var released = await InsertInvoiceAsync(repository, Released);
            var before = await RawAsync(database, released.RtId);
            var number = before["attributes"]["number"].AsString;

            var exception = await Record.ExceptionAsync(() => ImportTextAsync(repository,
                InvoiceYaml(released.RtId, "changed", "Released", number), ImportStrategy.Upsert));

            var refused = Assert.IsType<ReleasedDocumentLockedException>(exception);
            Assert.Equal(["Title"], Assert.Single(refused.Refusals).Changes);
            Assert.Equal(before, await RawAsync(database, released.RtId));
            var denied = Assert.Single(await DeniedEntriesAsync(database, released.RtId))["attributes"].AsBsonDocument;
            Assert.Equal(["Title"], denied["attributeNames"].AsBsonArray.Select(v => v.AsString));
        });
    }

    /// <summary>
    ///     Abuse cases 10, 11: an import (Insert or Upsert) and the seed / re-apply entry of the blueprint engine
    ///     (<c>ImportModelAsync</c>, Upsert) that carry the stored content are a no-op: no write, no Denied entry.
    /// </summary>
    [Theory]
    [InlineData(ImportStrategy.Upsert)]
    [InlineData(ImportStrategy.Insert)]
    public async Task ImportOfTheStoredContent_IsANoOp(ImportStrategy strategy)
    {
        await WithTenantAsync("relnoop", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var released = await InsertInvoiceAsync(repository, Released);
            var before = await RawAsync(database, released.RtId);
            var number = before["attributes"]["number"].AsString;

            await ImportTextAsync(repository, InvoiceYaml(released.RtId, "invoice", "Released", number), strategy);
            await ImportModelAsync(repository, released.RtId, "invoice", number);

            Assert.Equal(before, await RawAsync(database, released.RtId));
            Assert.Empty(await DeniedEntriesAsync(database, released.RtId));
        });
    }

    /// <summary>
    ///     Abuse case 11: the seed install and the re-apply of a blueprint write through <c>ImportModelAsync</c> with
    ///     Upsert; carrying a changed released document they are refused with a Denied entry and overwrite nothing.
    /// </summary>
    [Fact]
    public async Task SeedOrReApply_ThatChangesAReleasedDocument_IsRefused()
    {
        await WithTenantAsync("relseed", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var released = await InsertInvoiceAsync(repository, Released);
            var before = await RawAsync(database, released.RtId);

            var exception = await Record.ExceptionAsync(() =>
                ImportModelAsync(repository, released.RtId, "seeded", before["attributes"]["number"].AsString));

            Assert.IsType<ReleasedDocumentLockedException>(exception);
            Assert.Equal(before, await RawAsync(database, released.RtId));
            Assert.Single(await DeniedEntriesAsync(database, released.RtId));
        });
    }

    /// <summary>
    ///     An import of a released document that the tenant does not hold yet inserts it with the number of the file; the
    ///     counter is not touched (no renumbering, no advance).
    /// </summary>
    [Fact]
    public async Task ImportOfANewReleasedDocument_KeepsTheNumberOfTheFile_AndLeavesTheCounter()
    {
        await WithTenantAsync("relnew", async (repository, database) =>
        {
            var rangeId = await InsertRangeAsync(repository);
            var rangeBefore = await RawAsync(database, rangeId);
            var rtId = OctoObjectId.GenerateNewId();

            await ImportTextAsync(repository, InvoiceYaml(rtId, "imported", "Released", "RE-2025-00042"),
                ImportStrategy.Upsert);

            Assert.Equal("RE-2025-00042", (await RawAsync(database, rtId))["attributes"]["number"].AsString);
            Assert.Equal(rangeBefore, await RawAsync(database, rangeId));
        });
    }

    /// <summary>
    ///     Restore (outside the lock by design, no switch): a tenant with released documents, the counter with its
    ///     period and a Denied entry is dumped, dropped, restored with mongorestore and attached; everything is back
    ///     byte for byte, the documents are still locked, and the next released document gets the next number of its
    ///     range and fiscal year.
    /// </summary>
    [Fact]
    public async Task TenantDumpRestore_BringsEverythingBack_AndNumberingGoesOn()
    {
        var systemContext = fixture.GetSystemContext();
        var opsService = fixture.Provider!.GetRequiredService<IRepositoryOpsService>();
        var tenantId = $"relrestore{Guid.NewGuid():N}"[..20];
        var databaseName = tenantId.ToLowerInvariant();
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"relrestore_{Guid.NewGuid():N}");
        var archive = Path.Combine(backupDirectory, "backup.tar.gz");
        try
        {
            var repository = await CreateTenantAsync(tenantId);
            var database = GetTenantDatabase(tenantId);
            var rangeId = await InsertRangeAsync(repository);
            var first = await InsertInvoiceAsync(repository, Released);
            var second = await InsertInvoiceAsync(repository, Released);
            var draft = await InsertInvoiceAsync(repository, Draft);
            await Record.ExceptionAsync(() => WriteAsync(repository, "ApplyChanges update", first.RtId));
            var before = new[]
            {
                await RawAsync(database, first.RtId), await RawAsync(database, second.RtId),
                await RawAsync(database, rangeId)
            };
            var deniedBefore = Assert.Single(await DeniedEntriesAsync(database, first.RtId));

            Directory.CreateDirectory(backupDirectory);
            var dump = await opsService.ExecuteMongoDumpAsync(MongoDumpOptions.ForArchive(databaseName, archive));
            Assert.True(dump.Success, dump.Error);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            var restore = await opsService.ExecuteMongoRestoreAsync(new MongoRestoreOptions
            {
                Drop = true, Archive = archive, Database = databaseName, Gzip = true
            }, TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.True(restore.Success, restore.Error);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, databaseName, tenantId);
                await session.CommitTransactionAsync();
            }

            var restored = (await systemContext.GetChildTenantContextAsync(tenantId)).GetTenantRepository();
            Assert.Equal(before, new[]
            {
                await RawAsync(database, first.RtId), await RawAsync(database, second.RtId),
                await RawAsync(database, rangeId)
            });
            Assert.Equal(deniedBefore, Assert.Single(await DeniedEntriesAsync(database, first.RtId)));

            await Assert.ThrowsAsync<ReleasedDocumentLockedException>(() =>
                WriteAsync(restored, "UpdateOneRtEntityById", second.RtId));
            var release = Partial(draft.RtId, (StateAttribute, Released));
            await ApplyAsync(restored, EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release));
            var numbers = new[] { first.RtId, second.RtId, draft.RtId }
                .Select(id => RawAsync(database, id).GetAwaiter().GetResult()["attributes"]["number"].AsString).ToList();
            Assert.EndsWith("-00001", numbers[0]);
            Assert.EndsWith("-00002", numbers[1]);
            Assert.EndsWith("-00003", numbers[2]);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, true);
            }
        }
    }

    /// <summary>
    ///     Storno AC 1 and 4: a credit note that cancels a released invoice is created as a draft with its
    ///     <c>System/Cancels</c> association and released; it gets its own number from its own range, the invoice is byte
    ///     for byte the same, and the invoice finds the credit note through the inbound association.
    /// </summary>
    [Fact]
    public async Task Storno_OfAReleasedInvoice_GetsItsOwnNumber_AndTheOriginalStaysUnchanged()
    {
        await WithTenantAsync("relstorno", async (repository, database) =>
        {
            await InsertRangeAsync(repository, storno: "ReleaseTest/CreditNote");
            await InsertRangeAsync(repository, wellKnownName: "CreditNoteNumber");
            var invoice = await InsertInvoiceAsync(repository, Released);
            var before = await RawAsync(database, invoice.RtId);

            var creditNote = await InsertCreditNoteAsync(repository, invoice.RtId, Draft);
            var release = new RtEntity(CreditNoteTypeId, creditNote.RtId,
                new Dictionary<string, object?> { [StateAttribute] = Released });
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release));

            Assert.EndsWith("-00001", (await RawAsync(database, creditNote.RtId))["attributes"]["number"].AsString);
            Assert.Equal(before, await RawAsync(database, invoice.RtId));
            using var session = await repository.GetSessionAsync();
            var inbound = await repository.GetRtAssociationsAsync(session,
                [new RtEntityId(InvoiceTypeId, invoice.RtId)],
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, CancelsRoleId));
            var cancelledBy = Assert.Single(inbound.Values.SelectMany(r => r.Items));
            Assert.Equal(creditNote.RtId, cancelledBy.OriginRtId);
        });
    }

    /// <summary>Storno AC 2 (D7): a storno of a draft invoice is refused with message 169; nothing is written.</summary>
    [Fact]
    public async Task Storno_OfADraft_IsRefused()
    {
        await WithTenantAsync("relsdraft", async (repository, database) =>
        {
            await InsertRangeAsync(repository, storno: "ReleaseTest/CreditNote");
            await InsertRangeAsync(repository, wellKnownName: "CreditNoteNumber");
            var draft = await InsertInvoiceAsync(repository, Draft);

            var exception = await Record.ExceptionAsync(() => InsertCreditNoteAsync(repository, draft.RtId, Draft));

            var refused = Assert.IsType<StornoRefusedException>(exception);
            Assert.Equal(169, Assert.Single(refused.OperationResult.Messages).MessageNumber);
            Assert.Empty(await CreditNotesOfAsync(repository, draft.RtId));
        });
    }

    /// <summary>
    ///     Storno AC 3 (D6): an invoice with a released credit note gets no second one - neither a new credit note
    ///     linked to it nor a second draft credit note that is released later (message 170).
    /// </summary>
    [Fact]
    public async Task SecondStorno_IsRefused()
    {
        await WithTenantAsync("relssecond", async (repository, _) =>
        {
            await InsertRangeAsync(repository, storno: "ReleaseTest/CreditNote");
            await InsertRangeAsync(repository, wellKnownName: "CreditNoteNumber");
            var invoice = await InsertInvoiceAsync(repository, Released);
            var otherDraft = await InsertCreditNoteAsync(repository, invoice.RtId, Draft);
            await InsertCreditNoteAsync(repository, invoice.RtId, Released);

            var second = await Record.ExceptionAsync(() => InsertCreditNoteAsync(repository, invoice.RtId, Released));
            Assert.Equal(170, Assert.Single(Assert.IsType<StornoRefusedException>(second).OperationResult.Messages)
                .MessageNumber);

            var release = new RtEntity(CreditNoteTypeId, otherDraft.RtId,
                new Dictionary<string, object?> { [StateAttribute] = Released });
            var late = await Record.ExceptionAsync(() =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release)));
            Assert.Equal(170, Assert.Single(Assert.IsType<StornoRefusedException>(late).OperationResult.Messages)
                .MessageNumber);
            Assert.Equal(2, (await CreditNotesOfAsync(repository, invoice.RtId)).Count);
        });
    }

    /// <summary>A storno of an invoice whose range configures no storno type is refused (message 172).</summary>
    [Fact]
    public async Task Storno_WithoutAConfiguredStornoType_IsRefused()
    {
        await WithTenantAsync("relsnone", async (repository, _) =>
        {
            await InsertRangeAsync(repository);
            await InsertRangeAsync(repository, wellKnownName: "CreditNoteNumber");
            var invoice = await InsertInvoiceAsync(repository, Released);

            var exception = await Record.ExceptionAsync(() => InsertCreditNoteAsync(repository, invoice.RtId, Draft));

            Assert.Equal(172, Assert.Single(Assert.IsType<StornoRefusedException>(exception).OperationResult.Messages)
                .MessageNumber);
        });
    }

    private static async Task<RtEntity> InsertCreditNoteAsync(ITenantRepository repository, OctoObjectId originalId,
        int state)
    {
        var creditNote = new RtEntity(CreditNoteTypeId, OctoObjectId.GenerateNewId(),
            new Dictionary<string, object?> { ["Title"] = "credit note", [StateAttribute] = state });
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(creditNote)],
            [
                AssociationUpdateInfo.CreateInsert(creditNote.ToRtEntityId(), new RtEntityId(InvoiceTypeId, originalId),
                    CancelsRoleId)
            ], operationResult);
        Assert.False(operationResult.HasErrors, operationResult.GetMessages());
        await session.CommitTransactionAsync();
        return creditNote;
    }

    private static async Task<List<RtAssociation>> CreditNotesOfAsync(ITenantRepository repository,
        OctoObjectId originalId)
    {
        using var session = await repository.GetSessionAsync();
        var inbound = await repository.GetRtAssociationsAsync(session, [new RtEntityId(InvoiceTypeId, originalId)],
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, CancelsRoleId));
        return inbound.Values.SelectMany(r => r.Items).ToList();
    }

    public static TheoryData<string> RefusedRangeChanges =>
    [
        "removes Release", "changes StateAttribute", "changes StornoCkTypeId", "removes a released value",
        "adds a changeable attribute", "deletes the range"
    ];

    /// <summary>
    ///     D11: after the first number of a range with a release configuration, every change that would unlock its
    ///     documents is refused with message 168 on the repository, the range stays byte for byte the same, and a Denied
    ///     entry names the range.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedRangeChanges))]
    public async Task NumberedRange_RefusesTheChange(string change)
    {
        await WithTenantAsync("relrange", async (repository, database) =>
        {
            var rangeId = await InsertRangeAsync(repository, changeable: ["Title"], storno: "ReleaseTest/LegacyDoc");
            await InsertInvoiceAsync(repository, Released);
            var before = await RawAsync(database, rangeId);

            var exception = await Record.ExceptionAsync(() => WriteRangeAsync(repository, rangeId, change));

            var refused = Assert.IsType<ReleasedDocumentLockedException>(exception);
            Assert.Equal(168, Assert.Single(refused.OperationResult.Messages).MessageNumber);
            Assert.Equal(before, await RawAsync(database, rangeId));
            Assert.Single(await DeniedEntriesAsync(database, rangeId));
        });
    }

    /// <summary>D11: before the first number the release configuration stays freely configurable.</summary>
    [Theory]
    [MemberData(nameof(RefusedRangeChanges))]
    public async Task RangeBeforeTheFirstNumber_TakesEveryChange(string change)
    {
        await WithTenantAsync("relfree", async (repository, database) =>
        {
            var rangeId = await InsertRangeAsync(repository, changeable: ["Title"], storno: "ReleaseTest/LegacyDoc");
            await InsertInvoiceAsync(repository, Draft);
            var before = await RawAsync(database, rangeId);

            await WriteRangeAsync(repository, rangeId, change);

            Assert.NotEqual(before, await TryRawAsync(database, rangeId));
        });
    }

    /// <summary>D11: adding a released value and removing a changeable attribute lock more and are written.</summary>
    [Fact]
    public async Task NumberedRange_TakesChangesThatLockMore()
    {
        await WithTenantAsync("relmore", async (repository, database) =>
        {
            var rangeId = await InsertRangeAsync(repository, changeable: ["Title"], storno: "ReleaseTest/LegacyDoc");
            await InsertInvoiceAsync(repository, Released);

            await UpdateReleaseAsync(repository, rangeId,
                Release(["Released", "Cancelled", "Draft"], [], "ReleaseTest/LegacyDoc"));

            var release = (await RawAsync(database, rangeId))["attributes"]["release"]["attributes"].AsBsonDocument;
            Assert.Equal(3, release["releasedValues"].AsBsonArray.Count);
        });
    }

    /// <summary>
    ///     D11 on the import path: a seed re-apply or <c>importrt</c> Upsert of a numbered range that carries no release
    ///     configuration and a reset counter keeps the tenant's (the attributes are TenantOwned), and nothing is refused.
    /// </summary>
    [Fact]
    public async Task ImportOfANumberedRange_KeepsTheTenantsReleaseConfiguration()
    {
        await WithTenantAsync("relrimp", async (repository, database) =>
        {
            var rangeId = await InsertRangeAsync(repository, changeable: ["Title"]);
            await InsertInvoiceAsync(repository, Released);
            var before = (await RawAsync(database, rangeId))["attributes"].AsBsonDocument;

            await ImportTextAsync(repository, RangeYaml(rangeId), ImportStrategy.Upsert);

            var after = (await RawAsync(database, rangeId))["attributes"].AsBsonDocument;
            Assert.Equal(before["release"], after["release"]);
            Assert.Equal(before["currentValue"], after["currentValue"]);
            Assert.Equal(before["periods"], after["periods"]);
            Assert.Empty(await DeniedEntriesAsync(database, rangeId));
        });
    }

    private static string RangeYaml(OctoObjectId rangeId) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - System-2.9.0
        entities:
          - rtId: {{rangeId}}
            ckTypeId: System/AutoIncrement
            rtWellKnownName: InvoiceNumber
            attributes:
              - id: System/AutoIncrement.End
                value: 99999
              - id: System/AutoIncrement.CurrentValue
                value: 0
        """;

    private static async Task WriteRangeAsync(ITenantRepository repository, OctoObjectId rangeId, string change)
    {
        const string storno = "ReleaseTest/LegacyDoc";
        switch (change)
        {
            case "removes Release":
                await UpdateReleaseAsync(repository, rangeId, null);
                break;
            case "changes StateAttribute":
                await UpdateReleaseAsync(repository, rangeId, Release(["Released", "Cancelled"], ["Title"], storno,
                    "Title"));
                break;
            case "changes StornoCkTypeId":
                await UpdateReleaseAsync(repository, rangeId, Release(["Released", "Cancelled"], ["Title"],
                    "ReleaseTest/Invoice"));
                break;
            case "removes a released value":
                await UpdateReleaseAsync(repository, rangeId, Release(["Released"], ["Title"], storno));
                break;
            case "adds a changeable attribute":
                await UpdateReleaseAsync(repository, rangeId, Release(["Released", "Cancelled"],
                    ["Title", "DocumentState"], storno));
                break;
            case "deletes the range":
                using (var session = await repository.GetSessionAsync())
                {
                    session.StartTransaction();
                    await repository.DeleteOneRtEntityByRtIdAsync(session, AutoIncrementTypeId, rangeId,
                        DeleteOptions.Erase);
                    await session.CommitTransactionAsync();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private static async Task UpdateReleaseAsync(ITenantRepository repository, OctoObjectId rangeId,
        RtNumberRangeReleaseRecord? release)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var update = new RtEntity(AutoIncrementTypeId, rangeId);
        update.SetAttributeRawValue("Release", release);
        await repository.UpdateOneRtEntityByIdAsync(session, AutoIncrementTypeId, rangeId, update);
        await session.CommitTransactionAsync();
    }

    private static RtNumberRangeReleaseRecord Release(List<string> releasedValues, List<string> changeable,
        string? storno, string stateAttribute = StateAttribute) => new()
    {
        StateAttribute = stateAttribute,
        ReleasedValues = new AttributeStringValueList(releasedValues),
        ChangeableAttributes = new AttributeStringValueList(changeable),
        StornoCkTypeId = storno
    };

    private static async Task WriteAsync(ITenantRepository repository, string path, OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var changed = Partial(rtId, ("Title", "changed"));
        // The tenants of these tests hold one invoice with the title "invoice", so the filter matches exactly it.
        var byTitle = FieldFilterCriteria.Create().FieldEquals("Title", "invoice");
        switch (path)
        {
            case "ApplyChanges update":
                await repository.ApplyChangesAsync(session,
                    [EntityUpdateInfo<RtEntity>.CreateUpdate(changed.ToRtEntityId(), changed)], new OperationResult());
                break;
            case "UpdateOneRtEntityById":
                await repository.UpdateOneRtEntityByIdAsync(session, InvoiceTypeId, rtId, changed);
                break;
            case "UpdateOneRtEntity by filter":
                await repository.UpdateOneRtEntityAsync(session, byTitle, changed);
                break;
            case "UpdateManyRtEntity by filter":
                await repository.UpdateManyRtEntityAsync(session, byTitle, changed);
                break;
            case "ReplaceOneRtEntityById":
                await repository.ReplaceOneRtEntityByIdAsync(session, InvoiceTypeId, rtId, Invoice(Released, rtId, "changed"));
                break;
            case "ReplaceOneRtEntity by filter":
                await repository.ReplaceOneRtEntityAsync(session, byTitle, Invoice(Released, rtId, "changed"));
                break;
            case "DeleteOneRtEntityByRtId archive":
                await repository.DeleteOneRtEntityByRtIdAsync(session, InvoiceTypeId, rtId, DeleteOptions.Default);
                break;
            case "DeleteOneRtEntityByRtId erase":
                await repository.DeleteOneRtEntityByRtIdAsync(session, InvoiceTypeId, rtId, DeleteOptions.Erase);
                break;
            case "DeleteOneRtEntity by filter":
                await repository.DeleteOneRtEntityAsync(session, InvoiceTypeId, byTitle, DeleteOptions.Erase);
                break;
            case "DeleteManyRtEntities by filter":
                await repository.DeleteManyRtEntitiesAsync(session, InvoiceTypeId, byTitle, DeleteOptions.Erase);
                break;
            case "InsertOneRtEntity with the stored id":
                await repository.InsertOneRtEntityAsync(session, InvoiceTypeId, Invoice(Released, rtId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }

        await session.CommitTransactionAsync();
    }

    private async Task ImportTextAsync(ITenantRepository repository, string yaml, ImportStrategy strategy)
    {
        await repository.GetCkTypeGraphAsync(InvoiceTypeId);
        await fixture.GetService<IImportRtModelCommand>().ImportTextAsync(repository, yaml, strategy);
    }

    private async Task ImportModelAsync(ITenantRepository repository, OctoObjectId rtId, string title, string number)
    {
        var root = new RtModelRootTcDto
        {
            Dependencies = ["ReleaseTest-1.0.0"],
            Entities =
            [
                new RtEntityTcDto
                {
                    RtId = rtId,
                    CkTypeId = InvoiceTypeId,
                    Attributes =
                    [
                        new RtAttributeTcDto { Id = new RtCkId<CkAttributeId>("ReleaseTest/Title"), Value = title },
                        new RtAttributeTcDto { Id = new RtCkId<CkAttributeId>("ReleaseTest/DocumentState"), Value = "Released" },
                        new RtAttributeTcDto { Id = new RtCkId<CkAttributeId>("ReleaseTest/DocumentNumber"), Value = number }
                    ]
                }
            ]
        };
        await fixture.GetService<IImportRtModelCommand>().ImportModelAsync(repository, root, ImportStrategy.Upsert);
    }

    private static string InvoiceYaml(OctoObjectId rtId, string title, string state, string number) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - ReleaseTest-1.0.0
        entities:
          - rtId: {{rtId}}
            ckTypeId: ReleaseTest/Invoice
            attributes:
              - id: ReleaseTest/Title
                value: {{title}}
              - id: ReleaseTest/DocumentState
                value: {{state}}
              - id: ReleaseTest/DocumentNumber
                value: {{number}}
        """;

    private static async Task<OctoObjectId> InsertRangeAsync(ITenantRepository repository,
        List<string>? changeable = null, string? storno = null, string wellKnownName = "InvoiceNumber")
    {
        var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
        counter.RtId = OctoObjectId.GenerateNewId();
        counter.RtWellKnownName = wellKnownName;
        counter.Format = "RE-{yyyy}-{counter:D5}";
        counter.CurrentValue = 0;
        counter.End = 99999;
        counter.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
        counter.ReferenceTimeZone = "Europe/Vienna";
        counter.Release = new RtNumberRangeReleaseRecord
        {
            StateAttribute = StateAttribute,
            ReleasedValues = new AttributeStringValueList(["Released", "Cancelled"]),
            ChangeableAttributes = changeable == null ? null : new AttributeStringValueList(changeable),
            StornoCkTypeId = storno
        };
        await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(counter));
        return counter.RtId;
    }

    private static async Task<RtEntity> InsertInvoiceAsync(ITenantRepository repository, int state)
    {
        var invoice = Invoice(state, OctoObjectId.GenerateNewId());
        await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(invoice));
        return invoice;
    }

    private static async Task ApplyAsync(ITenantRepository repository, params IEntityUpdateInfo<RtEntity>[] updates)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, updates, operationResult);
        Assert.False(operationResult.HasErrors, operationResult.GetMessages());
        await session.CommitTransactionAsync();
    }

    private static RtEntity Invoice(int state, OctoObjectId rtId, string title = "invoice") => new(InvoiceTypeId, rtId,
        new Dictionary<string, object?> { ["Title"] = title, [StateAttribute] = state });

    private static RtEntity Partial(OctoObjectId rtId, params (string Name, object? Value)[] attributes)
    {
        var partial = new RtEntity(InvoiceTypeId, rtId);
        foreach (var (name, value) in attributes)
        {
            partial.SetAttributeRawValue(name, value);
        }

        return partial;
    }

    private static async Task<BsonDocument> RawAsync(IMongoDatabase database, OctoObjectId rtId) =>
        await TryRawAsync(database, rtId) ?? throw new InvalidOperationException($"Document {rtId} not found");

    private static async Task<BsonDocument?> TryRawAsync(IMongoDatabase database, OctoObjectId rtId)
    {
        var filter = new BsonDocument("_id", ObjectId.Parse(rtId.ToString()));
        foreach (var name in await EntityCollectionsAsync(database))
        {
            var document = await database.GetCollection<BsonDocument>(name).Find(filter)
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            if (document != null)
            {
                return document;
            }
        }

        return null;
    }

    private static async Task<List<BsonDocument>> DeniedEntriesAsync(IMongoDatabase database, OctoObjectId rtId)
    {
        var filter = new BsonDocument { ["attributes.documentRtId"] = rtId.ToString(), ["attributes.action"] = 2 };
        var entries = new List<BsonDocument>();
        foreach (var name in await EntityCollectionsAsync(database))
        {
            entries.AddRange(await database.GetCollection<BsonDocument>(name).Find(filter)
                .ToListAsync(TestContext.Current.CancellationToken));
        }

        return entries;
    }

    private static async Task<List<string>> EntityCollectionsAsync(IMongoDatabase database) =>
        (await (await database.ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken))
        .Where(n => n.StartsWith("RtEntity_", StringComparison.Ordinal)).ToList();

    private async Task<ITenantRepository> CreateTenantAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
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

        await tenant.ImportCkModelAsync(AutoIncrementReleaseTests.DocumentModel(SystemCkIds.CkModelId));
        return tenant.GetTenantRepository();
    }

    private async Task WithTenantAsync(string prefix, Func<ITenantRepository, IMongoDatabase, Task> body)
    {
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        try
        {
            var repository = await CreateTenantAsync(tenantId);
            await body(repository, GetTenantDatabase(tenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(fixture.GetSystemContext(), tenantId);
        }
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
}
