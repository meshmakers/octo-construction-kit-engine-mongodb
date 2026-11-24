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
///     AB#6209 against a real MongoDB, in throwaway child tenants: <c>AutoIncrementModifier</c> numbers every new
///     entity of a mixed batch (order-independent), never renumbers or clears the number of a stored entity (replace,
///     partial update) and keeps supplied values (ImportRt / restore) without advancing the counter.
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

            // Consecutive, but not in batch order: the entity rule engine collects inserts unordered.
            long[] numbers = [await NumberAsync(database, first.RtId), await NumberAsync(database, second.RtId)];
            Assert.Equal(new long[] { CounterStart + 1, CounterStart + 2 }, numbers.Order());
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
    ///     A batch without auto-increment types reads neither the counter nor the stored entities; the same probe sees
    ///     both reads for a batch with a Ticket (control).
    /// </summary>
    [Fact]
    public async Task BatchWithoutAutoIncrementTypes_IssuesNoExtraRead()
    {
        await WithTicketTenantAsync("ainoread", async (repository, database, _) =>
        {
            var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
            var databaseName = database.DatabaseNamespace.DatabaseName;

            var notesOnly = await MongoProfiler.ProfileReadsAsync(config, databaseName, () =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Note("one")),
                    EntityUpdateInfo<RtEntity>.CreateInsert(Note("two"))));
            var withTicket = await MongoProfiler.ProfileReadsAsync(config, databaseName, () =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(Note("three")),
                    EntityUpdateInfo<RtEntity>.CreateInsert(Ticket("ticket"))));

            Assert.DoesNotContain(notesOnly, IsCounterRead);
            Assert.DoesNotContain(notesOnly, IsReadByRtId);
            Assert.Contains(withTicket, IsCounterRead);
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
            var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
                fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName), databaseName,
                databaseName);
            var modifier = new AutoIncrementModifier(fixture.GetService<ICkCacheService>());
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

    private static string? ReadCollection(BsonDocument profiled)
    {
        var command = profiled["command"].AsBsonDocument;
        return command.TryGetValue("find", out var find) ? find.AsString
            : command.TryGetValue("aggregate", out var aggregate) && aggregate.IsString ? aggregate.AsString
            : null;
    }

    private static bool IsCounterRead(BsonDocument profiled) =>
        ReadCollection(profiled) == "RtEntity_SystemAutoIncrement";

    /// <summary>A read of the test model's collections that filters by <c>_id</c> with <c>$in</c> (the stored-entity check).</summary>
    private static bool IsReadByRtId(BsonDocument profiled) =>
        ReadCollection(profiled) is { } collection && collection.Contains("AutoIncrementTest", StringComparison.Ordinal) &&
        profiled["command"].ToJson().Contains("\"$in\"", StringComparison.Ordinal);

    private static RtEntity Note(string title) =>
        new(NoteTypeId, OctoObjectId.GenerateNewId(), new Dictionary<string, object?> { { "Title", title } });

    private static RtEntity Ticket(string title, long? number = null)
    {
        var attributes = new Dictionary<string, object?> { { "Title", title } };
        if (number != null)
        {
            attributes[NumberAttribute] = number.Value;
        }

        return new RtEntity(TicketTypeId, OctoObjectId.GenerateNewId(), attributes);
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
