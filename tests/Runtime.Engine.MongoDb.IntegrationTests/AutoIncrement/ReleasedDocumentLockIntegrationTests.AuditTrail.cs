using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     The document audit trail against a real MongoDB: the <c>Release</c> entry is written with the number in the
///     transaction of the write that releases the document (an abort leaves none), a storno writes a <c>Storno</c> entry on
///     the original and on the storno that reference each other, and no caller - also no system session - can create,
///     change or delete an entry through the write funnel or the RT import (message 173).
/// </summary>
public partial class ReleasedDocumentLockIntegrationTests
{
    private const int ReleaseAction = 0;
    private const int StornoAction = 1;
    private static readonly RtCkId<CkTypeId> AuditEntryTypeId = new("System/DocumentAuditEntry");
    private static readonly RtSecurityContext Accountant = RtSecurityContext.ForUser("accountant-1", []);

    /// <summary>
    ///     Release criterion: releasing a draft writes one Release entry with the document, the caller's subject, the UTC
    ///     time, the number as written, the fiscal year and the counter value; the draft itself had none.
    /// </summary>
    [Fact]
    public async Task ReleasingADraft_WritesOneReleaseEntry_WithTheNumber()
    {
        await WithTenantAsync("audrel", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var draft = await InsertInvoiceAsync(repository, Draft);
            Assert.Empty(await AuditEntriesAsync(database, draft.RtId));

            var before = DateTime.UtcNow.AddSeconds(-1);
            var release = Partial(draft.RtId, (StateAttribute, Released));
            await ApplyAsAsync(repository, Accountant,
                EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release));

            var number = (await RawAsync(database, draft.RtId))["attributes"]["number"].AsString;
            var entry = Assert.Single(await AuditEntriesAsync(database, draft.RtId))["attributes"].AsBsonDocument;
            Assert.Equal(ReleaseAction, entry["action"].ToInt32());
            Assert.Equal(InvoiceTypeId.FullName, entry["documentCkTypeId"].AsString);
            Assert.Equal("accountant-1", entry["subjectId"].AsString);
            Assert.Equal(number, entry["documentNumber"].AsString);
            Assert.Equal(1, entry["sequenceValue"].ToInt32());
            Assert.Contains(entry["fiscalYear"].ToInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                number);
            Assert.InRange(entry["timestamp"].ToUniversalTime(), before, DateTime.UtcNow.AddSeconds(1));
            Assert.False(entry.Contains("source"));
        });
    }

    /// <summary>A document inserted directly in a released state gets its number and its Release entry at once.</summary>
    [Fact]
    public async Task InsertingAReleasedDocument_WritesItsReleaseEntry()
    {
        await WithTenantAsync("audins", async (repository, database) =>
        {
            await InsertRangeAsync(repository);

            var released = await InsertInvoiceAsync(repository, Released);

            var entry = Assert.Single(await AuditEntriesAsync(database, released.RtId))["attributes"].AsBsonDocument;
            Assert.Equal(ReleaseAction, entry["action"].ToInt32());
            Assert.Equal((await RawAsync(database, released.RtId))["attributes"]["number"].AsString,
                entry["documentNumber"].AsString);
            Assert.False(entry.Contains("subjectId")); // a system session has no subject
        });
    }

    /// <summary>
    ///     Release criterion, rollback: the entry is written in the transaction of the release; when the caller aborts
    ///     it, neither the number nor the entry stays.
    /// </summary>
    [Fact]
    public async Task AnAbortedRelease_LeavesNoReleaseEntry()
    {
        await WithTenantAsync("audabort", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var draft = await InsertInvoiceAsync(repository, Draft);

            var release = Partial(draft.RtId, (StateAttribute, Released));
            using (var session = await repository.GetSessionAsync(Accountant))
            {
                session.StartTransaction();
                var operationResult = new OperationResult();
                await repository.ApplyChangesAsync(session,
                    [EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release)], operationResult);
                Assert.False(operationResult.HasErrors, operationResult.GetMessages());
                await session.AbortTransactionAsync();
            }

            Assert.False((await RawAsync(database, draft.RtId))["attributes"].AsBsonDocument.Contains("number"));
            Assert.Empty(await AuditEntriesAsync(database, draft.RtId));
        });
    }

    /// <summary>
    ///     Storno criterion: releasing a credit note that cancels a released invoice writes one Storno entry on the invoice
    ///     (referencing the credit note) and one on the credit note (referencing the invoice), next to the credit note's
    ///     own Release entry; a draft credit note writes none.
    /// </summary>
    [Fact]
    public async Task ReleasingAStorno_WritesAStornoEntryOnTheOriginalAndOnTheStorno()
    {
        await WithTenantAsync("audstorno", async (repository, database) =>
        {
            await InsertRangeAsync(repository, storno: "ReleaseTest/CreditNote");
            await InsertRangeAsync(repository, wellKnownName: "CreditNoteNumber");
            var invoice = await InsertInvoiceAsync(repository, Released);
            var creditNote = await InsertCreditNoteAsync(repository, invoice.RtId, Draft);
            Assert.Empty(await AuditEntriesAsync(database, creditNote.RtId, StornoAction));

            var release = new RtEntity(CreditNoteTypeId, creditNote.RtId,
                new Dictionary<string, object?> { [StateAttribute] = Released });
            await ApplyAsAsync(repository, Accountant,
                EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release));

            var onOriginal = Assert.Single(await AuditEntriesAsync(database, invoice.RtId, StornoAction))["attributes"]
                .AsBsonDocument;
            Assert.Equal(creditNote.RtId.ToString(), onOriginal["referenceRtId"].AsString);
            Assert.Equal("accountant-1", onOriginal["subjectId"].AsString);
            var onStorno = Assert.Single(await AuditEntriesAsync(database, creditNote.RtId, StornoAction))["attributes"]
                .AsBsonDocument;
            Assert.Equal(invoice.RtId.ToString(), onStorno["referenceRtId"].AsString);
            Assert.Equal(CreditNoteTypeId.FullName, onStorno["documentCkTypeId"].AsString);
            Assert.Single(await AuditEntriesAsync(database, creditNote.RtId, ReleaseAction));
        });
    }

    /// <summary>A credit note inserted released together with its Cancels association writes both Storno entries.</summary>
    [Fact]
    public async Task InsertingAReleasedStorno_WritesBothStornoEntries()
    {
        await WithTenantAsync("audstins", async (repository, database) =>
        {
            await InsertRangeAsync(repository, storno: "ReleaseTest/CreditNote");
            await InsertRangeAsync(repository, wellKnownName: "CreditNoteNumber");
            var invoice = await InsertInvoiceAsync(repository, Released);

            var creditNote = await InsertCreditNoteAsync(repository, invoice.RtId, Released);

            Assert.Single(await AuditEntriesAsync(database, invoice.RtId, StornoAction));
            Assert.Single(await AuditEntriesAsync(database, creditNote.RtId, StornoAction));
        });
    }

    /// <summary>
    ///     Append-only criterion and the lock against creating entries: an update, a replace, a delete of a stored entry
    ///     and an insert of a new one are refused with message 173 for a user and for a system session; the stored entry
    ///     stays as it is and no entry is added.
    /// </summary>
    [Fact]
    public async Task NoCaller_CreatesChangesOrDeletesAnAuditEntry()
    {
        await WithTenantAsync("audlock", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var released = await InsertInvoiceAsync(repository, Released);
            var stored = Assert.Single(await AuditEntriesAsync(database, released.RtId));
            var entryId = new OctoObjectId(stored["_id"].AsObjectId.ToString());
            var entryRtId = new RtEntityId(AuditEntryTypeId, entryId);
            // Complete, valid entries, so only the append-only lock can refuse them (not the rule validation).
            Dictionary<string, object?> Valid(string number) => new()
            {
                ["DocumentRtId"] = released.RtId.ToString(), ["DocumentCkTypeId"] = InvoiceTypeId.FullName,
                ["Action"] = ReleaseAction, ["Timestamp"] = DateTime.UtcNow, ["DocumentNumber"] = number
            };
            var forged = new RtEntity(AuditEntryTypeId, entryId, Valid("forged"));
            var created = new RtEntity(AuditEntryTypeId, OctoObjectId.GenerateNewId(), Valid("forged too"));
            IEntityUpdateInfo<RtEntity>[] writes =
            [
                EntityUpdateInfo<RtEntity>.CreateUpdate(entryRtId, forged),
                EntityUpdateInfo<RtEntity>.CreateReplace(entryRtId, forged),
                EntityUpdateInfo<RtEntity>.CreateDelete(entryRtId),
                EntityUpdateInfo<RtEntity>.CreateInsert(created)
            ];

            foreach (var caller in new[] { Accountant, RtSecurityContext.System })
            {
                foreach (var write in writes)
                {
                    var exception = await Record.ExceptionAsync(() => ApplyAsAsync(repository, caller, write));
                    var refused = exception as DocumentAuditEntryAppendOnlyException;
                    Assert.True(refused != null,
                        $"write {Array.IndexOf(writes, write)} as '{caller.SubjectId ?? "system"}': {exception}");
                    Assert.Equal(173, Assert.Single(refused.OperationResult.Messages).MessageNumber);
                }
            }

            Assert.Equal(stored, Assert.Single(await AuditEntriesAsync(database, released.RtId)));
        });
    }

    /// <summary>An RT import of an entry the tenant does not hold is refused: only the engine creates entries.</summary>
    [Fact]
    public async Task ImportOfANewAuditEntry_IsRefused()
    {
        await WithTenantAsync("audimp", async (repository, database) =>
        {
            await InsertRangeAsync(repository);
            var released = await InsertInvoiceAsync(repository, Released);
            var root = new RtModelRootTcDto
            {
                Dependencies = ["ReleaseTest-1.0.0"],
                Entities =
                [
                    new RtEntityTcDto
                    {
                        RtId = OctoObjectId.GenerateNewId(),
                        CkTypeId = AuditEntryTypeId,
                        Attributes =
                        [
                            Attribute("DocumentRtId", released.RtId.ToString()),
                            Attribute("DocumentCkTypeId", InvoiceTypeId.FullName),
                            Attribute("Action", "Release"),
                            Attribute("Timestamp", DateTime.UtcNow)
                        ]
                    }
                ]
            };
            await repository.GetCkTypeGraphAsync(InvoiceTypeId);

            var exception = await Record.ExceptionAsync(() =>
                fixture.GetService<IImportRtModelCommand>().ImportModelAsync(repository, root, ImportStrategy.Upsert));

            var refused = Assert.IsType<DocumentAuditEntryAppendOnlyException>(exception);
            Assert.Equal(173, Assert.Single(refused.OperationResult.Messages).MessageNumber);
            Assert.Single(await AuditEntriesAsync(database, released.RtId));
        });

        static RtAttributeTcDto Attribute(string name, object value) =>
            new() { Id = new RtCkId<CkAttributeId>($"System/DocumentAuditEntry.{name}"), Value = value };
    }

    private static async Task ApplyAsAsync(ITenantRepository repository, RtSecurityContext caller,
        params IEntityUpdateInfo<RtEntity>[] updates)
    {
        using var session = await repository.GetSessionAsync(caller);
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, updates, operationResult);
        Assert.False(operationResult.HasErrors, operationResult.GetMessages());
        await session.CommitTransactionAsync();
    }

    private static async Task<List<BsonDocument>> AuditEntriesAsync(IMongoDatabase database, OctoObjectId documentRtId,
        int? action = null)
    {
        var filter = new BsonDocument { ["attributes.documentRtId"] = documentRtId.ToString() };
        if (action != null)
        {
            filter["attributes.action"] = action.Value;
        }

        var entries = new List<BsonDocument>();
        foreach (var name in await EntityCollectionsAsync(database))
        {
            entries.AddRange(await database.GetCollection<BsonDocument>(name).Find(filter)
                .ToListAsync(TestContext.Current.CancellationToken));
        }

        return entries;
    }
}
