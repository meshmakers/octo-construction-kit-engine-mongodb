using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     AB#6209 and AB#9753 against a real MongoDB, in throwaway child tenants: <c>AutoIncrementModifier</c> numbers
///     every new entity of a mixed batch in batch order, never renumbers or clears the number of a stored entity
///     (replace, partial update) and keeps supplied values (ImportRt / restore) without advancing the counter. The
///     numbers come from one atomic <c>findOneAndUpdate</c> with <c>$inc</c> per counter and batch: parallel writers
///     get distinct consecutive numbers, an aborted transaction rolls the counter back, a transient error gives exactly
///     one number after the retry, and <c>End</c> is never passed.
///     Model: Note (no auto-increment) and Ticket (<c>Number</c> from the counter "TicketNumber", which starts at 41).
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class AutoIncrementModifierTests(CkModelImportGuardFixture fixture)
{
    private const int CounterStart = 41;
    private const string NumberAttribute = "Number";

    private static readonly RtCkId<CkTypeId> NoteTypeId = new("AutoIncrementTest/Note");
    private static readonly RtCkId<CkTypeId> TicketTypeId = new("AutoIncrementTest/Ticket");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedInsert_NumbersTheTicket_InBothOrders(bool noteFirst)
    {
        await WithTicketTenantAsync("aimixed", async (repository, database, counterId) =>
        {
            var note = Note("note");
            var ticket = Ticket("ticket");
            var batch = noteFirst ? new[] { note, ticket } : new[] { ticket, note };

            await ApplyAsync(repository, batch.Select(EntityUpdateInfo<RtEntity>.CreateInsert).ToArray());

            Assert.Equal(CounterStart + 1, await NumberAsync(database, ticket.RtId));
            Assert.False((await FindDocumentAsync(database, note.RtId))["attributes"].AsBsonDocument.Contains("number"));
            Assert.Equal(CounterStart + 1, await CounterAsync(database, counterId));
        });
    }

    [Fact]
    public async Task TwoTicketsWithANoteInBetween_AreNumberedConsecutively()
    {
        await WithTicketTenantAsync("aitwo", async (repository, database, counterId) =>
        {
            var first = Ticket("first");
            var second = Ticket("second");

            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(first),
                EntityUpdateInfo<RtEntity>.CreateInsert(Note("between")),
                EntityUpdateInfo<RtEntity>.CreateInsert(second));

            // AB#9753: in batch order (the entity rule engine keeps the input order).
            Assert.Equal(CounterStart + 1, await NumberAsync(database, first.RtId));
            Assert.Equal(CounterStart + 2, await NumberAsync(database, second.RtId));
            Assert.Equal(CounterStart + 2, await CounterAsync(database, counterId));
        });
    }

    /// <summary>
    ///     A replace never renumbers: with the number in the payload it is kept, without it the stored number is
    ///     carried over (alone, and in a batch with other types and with a new Ticket that does get a number).
    /// </summary>
    [Fact]
    public async Task Replace_KeepsTheNumber_AndDoesNotAdvanceTheCounter()
    {
        await WithTicketTenantAsync("aireplace", async (repository, database, counterId) =>
        {
            var ticket = Ticket("ticket");
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(ticket));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, ticket.RtId));

            // Alone, payload without the number.
            await ApplyAsync(repository, Replace(ticket.RtId, "replaced without number"));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, ticket.RtId));
            Assert.Equal("replaced without number", await TitleAsync(database, ticket.RtId));

            // Alone, payload with the number.
            await ApplyAsync(repository, Replace(ticket.RtId, "replaced with number", CounterStart + 1));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, ticket.RtId));
            Assert.Equal(CounterStart + 1, await CounterAsync(database, counterId));

            // Mixed: replace of the Ticket next to a new Note and a new Ticket.
            var newTicket = Ticket("new ticket");
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Note("note")),
                Replace(ticket.RtId, "replaced in a mixed batch"),
                EntityUpdateInfo<RtEntity>.CreateInsert(newTicket));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, ticket.RtId));
            Assert.Equal("replaced in a mixed batch", await TitleAsync(database, ticket.RtId));
            Assert.Equal(CounterStart + 2, await NumberAsync(database, newTicket.RtId));
            Assert.Equal(CounterStart + 2, await CounterAsync(database, counterId));
        });
    }

    [Fact]
    public async Task PartialUpdate_KeepsTheNumber()
    {
        await WithTicketTenantAsync("aiupdate", async (repository, database, counterId) =>
        {
            var ticket = Ticket("ticket");
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(ticket));

            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(
                new RtEntityId(TicketTypeId, ticket.RtId),
                new RtEntity(TicketTypeId, ticket.RtId, new Dictionary<string, object?> { { "Title", "updated" } })));

            Assert.Equal("updated", await TitleAsync(database, ticket.RtId));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, ticket.RtId));
            Assert.Equal(CounterStart + 1, await CounterAsync(database, counterId));
        });
    }

    /// <summary>ImportRt / restore: a supplied number is kept and does not advance the counter.</summary>
    [Fact]
    public async Task InsertWithANumber_KeepsIt_AndDoesNotAdvanceTheCounter()
    {
        await WithTicketTenantAsync("aipreset", async (repository, database, counterId) =>
        {
            var ticket = Ticket("imported", 777);

            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(ticket));

            Assert.Equal(777, await NumberAsync(database, ticket.RtId));
            Assert.Equal(CounterStart, await CounterAsync(database, counterId));
        });
    }

    /// <summary>A mixed import file: present numbers are kept, absent ones are assigned.</summary>
    [Fact]
    public async Task MixedImport_KeepsPresentNumbers_AndNumbersTheOthers()
    {
        await WithTicketTenantAsync("aiimport", async (repository, database, counterId) =>
        {
            var withNumber = Ticket("with number", 500);
            var withoutNumber = Ticket("without number");

            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Note("note")),
                EntityUpdateInfo<RtEntity>.CreateInsert(withNumber),
                EntityUpdateInfo<RtEntity>.CreateInsert(withoutNumber));

            Assert.Equal(500, await NumberAsync(database, withNumber.RtId));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, withoutNumber.RtId));
            Assert.Equal(CounterStart + 1, await CounterAsync(database, counterId));
        });
    }

    [Fact]
    public async Task CounterAtEnd_Throws_AutoIncrementEndReached()
    {
        await WithTicketTenantAsync("aiend", async (repository, database, counterId) =>
        {
            var ticket = Ticket("one too many");

            var exception = await Record.ExceptionAsync(() =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(ticket)));

            Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Equal(CounterStart, await CounterAsync(database, counterId));
        }, end: CounterStart);
    }

    /// <summary>
    ///     A batch without auto-increment types touches neither the counter nor reads the stored entities; the same
    ///     probe sees the counter's findAndModify and the stored-entity read for a batch with a Ticket (control).
    /// </summary>
    [Fact]
    public async Task BatchWithoutAutoIncrementTypes_IssuesNoExtraRead()
    {
        await WithTicketTenantAsync("ainoread", async (repository, database, _) =>
        {
            var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
            var databaseName = database.DatabaseNamespace.DatabaseName;

            var notesOnly = await MongoProfiler.ProfileAllAsync(config, databaseName, () =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Note("one")),
                    EntityUpdateInfo<RtEntity>.CreateInsert(Note("two"))));
            var withTicket = await MongoProfiler.ProfileAllAsync(config, databaseName, () =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Note("three")),
                    EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("ticket"))));

            Assert.DoesNotContain(notesOnly, IsCounterOperation);
            Assert.DoesNotContain(notesOnly, IsReadByRtId);
            Assert.Contains(withTicket, IsCounterOperation);
            Assert.Contains(withTicket, IsReadByRtId);
        });
    }

    /// <summary>
    ///     ImportRt re-imports existing entities through the insert path (bulk upsert), so the modifier sees stored
    ///     entities among the "inserts": an exported number is kept, a stored number is carried over, a new entity is
    ///     numbered only when it has no value. Called directly, the way <c>BulkRtMutation</c> calls it.
    /// </summary>
    [Fact]
    public async Task ImportOfExistingEntities_KeepsTheirNumbers_AndNumbersOnlyNewOnes()
    {
        await WithTicketTenantAsync("aiexisting", async (repository, database, counterId) =>
        {
            var storedA = Ticket("stored a");
            var storedB = Ticket("stored b");
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(storedA),
                EntityUpdateInfo<RtEntity>.CreateInsert(storedB));
            var numberB = await NumberAsync(database, storedB.RtId);

            var importedA = Ticket("imported a", 99);
            importedA.RtId = storedA.RtId;
            var importedB = Ticket("imported b");
            importedB.RtId = storedB.RtId;
            var newWithNumber = Ticket("new with number", 500);
            var newWithoutNumber = Ticket("new without number");
            var note = Note("note");

            var databaseName = database.DatabaseNamespace.DatabaseName;
            var dataSource = DataSource(databaseName);
            var modifier = NewModifier();
            using (var session = await dataSource.GetSessionAsync())
            {
                session.StartTransaction();
                await modifier.RunAsync(session, dataSource,
                    [note, importedA, importedB, newWithNumber, newWithoutNumber]);
                await session.CommitTransactionAsync();
            }

            Assert.Equal(99L, Convert.ToInt64(importedA.GetAttributeValueOrDefault(NumberAttribute)));
            Assert.Equal(numberB, Convert.ToInt64(importedB.GetAttributeValueOrDefault(NumberAttribute)));
            Assert.Equal(500L, Convert.ToInt64(newWithNumber.GetAttributeValueOrDefault(NumberAttribute)));
            Assert.Equal(CounterStart + 3, Convert.ToInt64(newWithoutNumber.GetAttributeValueOrDefault(NumberAttribute)));
            Assert.Null(note.GetAttributeValueOrDefault(NumberAttribute));
            Assert.Equal(CounterStart + 3, await CounterAsync(database, counterId));
        });
    }

    // ---------------------------------------------------------------- AB#9753: atomic allocation

    /// <summary>
    ///     One batch with five Tickets (and Notes in between) is numbered n+1 .. n+5 in batch order, with exactly one
    ///     operation on the counter: a findAndModify that adds 5 (since AB#9755 a pipeline update with the
    ///     <c>StartValue</c> floor) - no read, no separate update and no replace.
    /// </summary>
    [Fact]
    public async Task Batch_IsNumberedInBatchOrder_WithOneFindAndModifyAndNoReplace()
    {
        await WithTicketTenantAsync("aiblock", async (repository, database, counterId) =>
        {
            var tickets = Enumerable.Range(1, 5).Select(i => Ticket($"ticket {i}")).ToList();
            var updates = new List<IEntityUpdateInfo<RtEntity>>();
            foreach (var ticket in tickets)
            {
                updates.Add(EntityUpdateInfo<RtEntity>.CreateInsert(ticket));
                updates.Add(EntityUpdateInfo<RtEntity>.CreateInsert(Note("between")));
            }

            var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
            var profiled = await MongoProfiler.ProfileAllAsync(config, database.DatabaseNamespace.DatabaseName,
                () => ApplyAsync(repository, updates.ToArray()));

            for (var i = 0; i < tickets.Count; i++)
            {
                Assert.Equal(CounterStart + 1 + i, await NumberAsync(database, tickets[i].RtId));
            }

            Assert.Equal(CounterStart + 5, await CounterAsync(database, counterId));
            var counterOperations = profiled.Where(IsCounterOperation).ToList();
            var findAndModify = Assert.Single(counterOperations);
            Assert.True(IsCounterFindAndModify(findAndModify), findAndModify.ToJson());
            // AB#9755: one pipeline update that sets the counter to max(counter, StartValue - 1) + 5 (the D5 floor).
            var stage = Assert.Single(findAndModify["command"]["update"].AsBsonArray).AsBsonDocument;
            var set = Assert.Single(stage.Elements);
            Assert.Equal("$set", set.Name);
            var assignment = Assert.Single(set.Value.AsBsonDocument.Elements);
            Assert.Equal("attributes.currentValue", assignment.Name);
            var add = assignment.Value["$add"].AsBsonArray;
            Assert.Equal(2, add.Count);
            Assert.True(add[0].AsBsonDocument.Contains("$max"), add.ToJson());
            Assert.Equal(new BsonInt64(5), add[1]);
        });
    }

    /// <summary>
    ///     AC 1: 20 creations in parallel, each in its own session and transaction, get 20 distinct numbers
    ///     n+1 .. n+20 and the counter ends at n+20. Parallel transactions on one counter conflict; the server aborts
    ///     the loser with <c>TransientTransactionError</c> and its owner (here the test, like a request handler) runs the
    ///     transaction again.
    /// </summary>
    [Fact]
    public async Task TwentyParallelCreations_InTransactions_GetTwentyDistinctConsecutiveNumbers()
    {
        await WithTicketTenantAsync("aipartx", async (repository, database, counterId) =>
        {
            var tickets = Enumerable.Range(1, 20).Select(i => Ticket($"parallel {i}")).ToList();

            await Task.WhenAll(tickets.Select(ticket => Task.Run(() =>
                ApplyWithTransactionRetryAsync(repository, () => Ticket("parallel", rtId: ticket.RtId)))));

            var numbers = new List<long>();
            foreach (var ticket in tickets)
            {
                numbers.Add(await NumberAsync(database, ticket.RtId));
            }

            Assert.Equal(Enumerable.Range(CounterStart + 1, 20).Select(n => (long)n), numbers.Order());
            Assert.Equal(CounterStart + 20, await CounterAsync(database, counterId));
        });
    }

    /// <summary>AC 1 without transactions: 20 parallel sessions, each $inc is atomic on its own.</summary>
    [Fact]
    public async Task TwentyParallelCreations_WithoutTransactions_GetTwentyDistinctConsecutiveNumbers()
    {
        await WithTicketTenantAsync("aiparnotx", async (repository, database, counterId) =>
        {
            var tickets = Enumerable.Range(1, 20).Select(i => Ticket($"parallel {i}")).ToList();

            await Task.WhenAll(tickets.Select(ticket => Task.Run(async () =>
            {
                using var session = await repository.GetSessionAsync();
                var operationResult = new OperationResult();
                await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(ticket)],
                    operationResult);
                Assert.False(operationResult.HasErrors, operationResult.GetMessages());
            })));

            var numbers = new List<long>();
            foreach (var ticket in tickets)
            {
                numbers.Add(await NumberAsync(database, ticket.RtId));
            }

            Assert.Equal(Enumerable.Range(CounterStart + 1, 20).Select(n => (long)n), numbers.Order());
            Assert.Equal(CounterStart + 20, await CounterAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 3 outside a transaction: a transient error injected on the first allocation attempt is retried by the
    ///     modifier; the entity gets exactly one number and the counter advances once.
    /// </summary>
    [Fact]
    public async Task TransientError_OutsideATransaction_IsRetried_AndGivesExactlyOneNumber()
    {
        await WithTicketTenantAsync("aitransient", async (_, database, counterId) =>
        {
            var attempts = 0;
            var modifier = NewModifier(() =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException(TransientError()) : Task.CompletedTask;
            });
            var ticket = Ticket("retried");

            var dataSource = DataSource(database.DatabaseNamespace.DatabaseName);
            using (var session = await dataSource.GetSessionAsync())
            {
                await modifier.RunAsync(session, dataSource, [ticket]);
            }

            Assert.Equal(2, attempts);
            Assert.Equal(CounterStart + 1, Convert.ToInt64(ticket.GetAttributeValueOrDefault(NumberAttribute)));
            Assert.Equal(CounterStart + 1, await CounterAsync(database, counterId));
        });
    }

    /// <summary>The retry outside a transaction is bounded: an error that stays gives up after the last attempt.</summary>
    [Fact]
    public async Task TransientError_OutsideATransaction_GivesUpAfterTheLastAttempt()
    {
        await WithTicketTenantAsync("aitransmax", async (_, database, counterId) =>
        {
            var attempts = 0;
            var modifier = NewModifier(() =>
            {
                attempts++;
                return Task.FromException(TransientError());
            });

            var dataSource = DataSource(database.DatabaseNamespace.DatabaseName);
            using var session = await dataSource.GetSessionAsync();
            var exception = await Record.ExceptionAsync(() =>
                modifier.RunAsync(session, dataSource, [Ticket("never numbered")]));

            AssertTransientTransactionErrorLabel(exception);
            Assert.Equal(AutoIncrementModifier.MaxAllocationAttempts, attempts);
            Assert.Equal(CounterStart, await CounterAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 3 inside a transaction: the modifier does not retry (the server has aborted the transaction), the
    ///     exception keeps its <c>TransientTransactionError</c> label for the owner, and nothing is committed.
    /// </summary>
    [Fact]
    public async Task TransientError_InsideATransaction_IsPassedOnToTheOwner_Unretried()
    {
        await WithTicketTenantAsync("aitransintx", async (_, database, counterId) =>
        {
            var attempts = 0;
            var modifier = NewModifier(() =>
            {
                attempts++;
                return Task.FromException(TransientError());
            });

            var dataSource = DataSource(database.DatabaseNamespace.DatabaseName);
            using (var session = await dataSource.GetSessionAsync())
            {
                session.StartTransaction();
                var exception = await Record.ExceptionAsync(() =>
                    modifier.RunAsync(session, dataSource, [Ticket("in a transaction")]));

                AssertTransientTransactionErrorLabel(exception);
                await session.AbortTransactionAsync();
            }

            Assert.Equal(1, attempts);
            Assert.Equal(CounterStart, await CounterAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 3 with a real conflict: a second transaction that allocates while the first one still holds its
    ///     uncommitted <c>$inc</c> is aborted with <c>TransientTransactionError</c>; run again after the first commits,
    ///     its entity gets exactly one number and the counter advances once per entity.
    /// </summary>
    [Fact]
    public async Task ConcurrentTransactions_TheLoserIsAbortedTransiently_AndItsRetryGetsExactlyOneNumber()
    {
        await WithTicketTenantAsync("aiconflict", async (repository, database, counterId) =>
        {
            var winner = Ticket("winner");
            var loser = Ticket("loser");

            using (var winnerSession = await repository.GetSessionAsync())
            {
                winnerSession.StartTransaction();
                var winnerResult = new OperationResult();
                await repository.ApplyChangesAsync(winnerSession, [EntityUpdateInfo<RtEntity>.CreateInsert(winner)],
                    winnerResult);
                Assert.False(winnerResult.HasErrors, winnerResult.GetMessages());

                using (var loserSession = await repository.GetSessionAsync())
                {
                    loserSession.StartTransaction();
                    var exception = await Record.ExceptionAsync(() => repository.ApplyChangesAsync(loserSession,
                        [EntityUpdateInfo<RtEntity>.CreateInsert(loser)], new OperationResult()));
                    AssertTransientTransactionErrorLabel(exception);
                }

                await winnerSession.CommitTransactionAsync();
            }

            // The owner runs the aborted transaction again, from its input.
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("loser", rtId: loser.RtId)));

            Assert.Equal(CounterStart + 1, await NumberAsync(database, winner.RtId));
            Assert.Equal(CounterStart + 2, await NumberAsync(database, loser.RtId));
            Assert.Equal(CounterStart + 2, await CounterAsync(database, counterId));
        });
    }

    /// <summary>AC 5: an aborted transaction rolls the counter back together with the entity.</summary>
    [Fact]
    public async Task AbortedTransaction_RollsTheCounterBack()
    {
        await WithTicketTenantAsync("aiabort", async (repository, database, counterId) =>
        {
            var ticket = Ticket("aborted");
            using (var session = await repository.GetSessionAsync())
            {
                session.StartTransaction();
                var operationResult = new OperationResult();
                await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(ticket)],
                    operationResult);
                Assert.False(operationResult.HasErrors, operationResult.GetMessages());
                Assert.Equal(CounterStart + 1, Convert.ToInt64(ticket.GetAttributeValueOrDefault(NumberAttribute)));
                await session.AbortTransactionAsync();
            }

            Assert.Equal(CounterStart, await CounterAsync(database, counterId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => FindDocumentAsync(database, ticket.RtId));

            // The next creation gets the number the aborted one had.
            var next = Ticket("next");
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(next));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, next.RtId));
        });
    }

    /// <summary>
    ///     AC 4 for a block: a batch that needs more numbers than are left raises <c>AutoIncrementEndReached</c> and
    ///     leaves the counter where it was (never past <c>End</c>, not partly advanced); the numbers that are left can
    ///     still be taken.
    /// </summary>
    [Fact]
    public async Task BatchLargerThanTheRest_Throws_AutoIncrementEndReached_AndDoesNotAdvance()
    {
        await WithTicketTenantAsync("aiendblock", async (repository, database, counterId) =>
        {
            var exception = await Record.ExceptionAsync(() => ApplyAsync(repository,
                EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("a")), EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("b")),
                EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("c"))));

            Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("number range 'TicketNumber'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("needs 3 number(s), 2 left", exception.Message, StringComparison.Ordinal);
            Assert.Equal(CounterStart, await CounterAsync(database, counterId));

            var last = Ticket("last");
            var beforeLast = Ticket("before last");
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(beforeLast),
                EntityUpdateInfo<RtEntity>.CreateInsert(last));
            Assert.Equal(CounterStart + 1, await NumberAsync(database, beforeLast.RtId));
            Assert.Equal(CounterStart + 2, await NumberAsync(database, last.RtId));
            Assert.Equal(CounterStart + 2, await CounterAsync(database, counterId));
        }, end: CounterStart + 2);
    }

    /// <summary>A Ticket whose counter does not exist raises <c>AutoIncrementReferenceNotFound</c> as before.</summary>
    [Fact]
    public async Task MissingCounter_Throws_AutoIncrementReferenceNotFound()
    {
        await WithTicketTenantAsync("ainocounter", async (repository, database, counterId) =>
        {
            await database.GetCollection<BsonDocument>(CounterCollection).DeleteOneAsync(
                new BsonDocument("_id", ObjectId.Parse(counterId.ToString())), TestContext.Current.CancellationToken);

            var exception = await Record.ExceptionAsync(() =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("orphan"))));

            Assert.IsType<InvalidAttributeException>(exception);
        });
    }

    private AutoIncrementModifier NewModifier(Func<Task>? beforeAllocationAttempt = null) =>
        new(fixture.GetService<ICkCacheService>(), NullLogger<AutoIncrementModifier>.Instance)
        {
            BeforeAllocationAttemptAsync = beforeAllocationAttempt
        };

    private MongoDbRepositoryDataSource DataSource(string databaseName) =>
        new(NullLogger<MongoDbRepositoryDataSource>.Instance,
            fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName), databaseName, databaseName);

    /// <summary>
    ///     The exception a transaction owner catches is the MongoDB exception itself, with the
    ///     <c>TransientTransactionError</c> label - not wrapped into an exception without it.
    /// </summary>
    private static void AssertTransientTransactionErrorLabel(Exception? exception)
    {
        var mongoException = Assert.IsAssignableFrom<MongoException>(exception);
        Assert.True(mongoException.HasErrorLabel("TransientTransactionError"), mongoException.ToString());
    }

    private static MongoException TransientError()
    {
        var exception = new MongoException("injected transient transaction error (test)");
        exception.AddErrorLabel("TransientTransactionError");
        return exception;
    }

    /// <summary>
    ///     What the owner of a transaction does on <c>TransientTransactionError</c>: run the whole transaction again
    ///     (bounded, with a short random pause so the parallel writers spread out).
    /// </summary>
    /// <remarks>
    ///     Every attempt builds the entity again from its input, as a request handler does: the modifier writes the
    ///     number into the entity object, so an aborted attempt's object carries a number that was rolled back.
    /// </remarks>
    private static async Task ApplyWithTransactionRetryAsync(ITenantRepository repository, Func<RtEntity> create)
    {
        const int maxAttempts = 100;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(create()));
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts &&
                                       RepositoryDistributedLockService.IsTransientTransactionError(ex))
            {
                await Task.Delay(Random.Shared.Next(5, 50));
            }
        }
    }

    private static string? ReadCollection(BsonDocument profiled)
    {
        var command = profiled["command"].AsBsonDocument;
        return command.TryGetValue("find", out var find) ? find.AsString
            : command.TryGetValue("aggregate", out var aggregate) && aggregate.IsString ? aggregate.AsString
            : null;
    }

    private const string CounterCollection = "RtEntity_SystemAutoIncrement";

    /// <summary>Any profiled operation on the counter collection (read, write or findAndModify).</summary>
    private static bool IsCounterOperation(BsonDocument profiled) =>
        profiled.TryGetValue("ns", out var ns) && ns.IsString &&
        ns.AsString.EndsWith("." + CounterCollection, StringComparison.Ordinal);

    private static bool IsCounterFindAndModify(BsonDocument profiled) =>
        IsCounterOperation(profiled) && profiled.TryGetValue("command", out var command) &&
        command.AsBsonDocument.Contains("findAndModify");

    /// <summary>A read of the test model's collections that filters by <c>_id</c> with <c>$in</c> (the stored-entity check).</summary>
    private static bool IsReadByRtId(BsonDocument profiled) =>
        ReadCollection(profiled) is { } collection && collection.Contains("AutoIncrementTest", StringComparison.Ordinal) &&
        profiled["command"].ToJson().Contains("\"$in\"", StringComparison.Ordinal);

    private static RtEntity Note(string title) =>
        new(NoteTypeId, OctoObjectId.GenerateNewId(), new Dictionary<string, object?> { { "Title", title } });

    private static RtEntity Ticket(string title, long? number = null, OctoObjectId? rtId = null)
    {
        var attributes = new Dictionary<string, object?> { { "Title", title } };
        if (number != null)
        {
            attributes[NumberAttribute] = number.Value;
        }

        return new RtEntity(TicketTypeId, rtId ?? OctoObjectId.GenerateNewId(), attributes);
    }

    private static IEntityUpdateInfo<RtEntity> Replace(OctoObjectId rtId, string title, long? number = null)
    {
        var replacement = Ticket(title, number);
        replacement.RtId = rtId;
        return EntityUpdateInfo<RtEntity>.CreateReplace(new RtEntityId(TicketTypeId, rtId), replacement);
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

    private static async Task<long> NumberAsync(IMongoDatabase database, OctoObjectId rtId) =>
        (await FindDocumentAsync(database, rtId))["attributes"]["number"].ToInt64();

    private static async Task<string> TitleAsync(IMongoDatabase database, OctoObjectId rtId) =>
        (await FindDocumentAsync(database, rtId))["attributes"]["title"].AsString;

    private static async Task<long> CounterAsync(IMongoDatabase database, OctoObjectId counterId) =>
        (await FindDocumentAsync(database, counterId))["attributes"]["currentValue"].ToInt64();

    private static async Task<BsonDocument> FindDocumentAsync(IMongoDatabase database, OctoObjectId rtId)
    {
        var filter = new BsonDocument("_id", ObjectId.Parse(rtId.ToString()));
        foreach (var name in await (await database.ListCollectionNamesAsync(
                     cancellationToken: TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken))
        {
            if (!name.StartsWith("RtEntity_", StringComparison.Ordinal))
            {
                continue;
            }

            var document = await database.GetCollection<BsonDocument>(name).Find(filter)
                .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
            if (document != null)
            {
                return document;
            }
        }

        throw new InvalidOperationException($"Entity {rtId} not found");
    }

    /// <summary>A throwaway tenant with the Note/Ticket model and the "TicketNumber" counter at <see cref="CounterStart" />.</summary>
    private async Task WithTicketTenantAsync(string prefix,
        Func<ITenantRepository, IMongoDatabase, OctoObjectId, Task> body, int end = 1000)
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

            await tenant.ImportCkModelAsync(TicketModel(SystemCkIds.CkModelId));

            var repository = tenant.GetTenantRepository();
            var counterId = OctoObjectId.GenerateNewId();
            using (var session = await repository.GetSessionAsync())
            {
                session.StartTransaction();
                var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
                counter.RtId = counterId;
                counter.RtWellKnownName = "TicketNumber";
                counter.CurrentValue = CounterStart;
                counter.End = end;
                await repository.InsertOneRtEntityAsync(session, counter);
                await session.CommitTransactionAsync();
            }

            await body(repository, GetTenantDatabase(tenantId), counterId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
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

    /// <summary>Note (no auto-increment) and Ticket (Number from the "TicketNumber" counter), on System <paramref name="system" />.</summary>
    private static CkCompiledModelRoot TicketModel(CkModelId system)
    {
        var id = new CkModelId("AutoIncrementTest-1.0.0");
        CkTypeAttributeDto Title() => new()
        {
            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Title-1")), AttributeName = "Title",
            IsOptional = true
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "AB#6209 auto-increment numbering in mixed batches",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Number-1"), ValueType = AttributeValueTypesDto.Int }
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Note-1"), IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
                    Attributes = [Title()]
                },
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Ticket-1"), IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
                    Attributes =
                    [
                        Title(),
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Number-1")),
                            AttributeName = NumberAttribute, IsOptional = true, AutoIncrementReference = "TicketNumber"
                        }
                    ]
                }
            ]
        };
    }
}
