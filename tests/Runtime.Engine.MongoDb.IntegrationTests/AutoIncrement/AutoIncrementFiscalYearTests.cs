using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     AB#9754 against a real MongoDB, in throwaway child tenants: number ranges per document type and fiscal year with
///     format tokens. The fiscal year comes from each entity's creation time (a fake clock) read in the range's
///     <c>ReferenceTimeZone</c>; every fiscal year has its own counter in <c>Periods</c>, advanced atomically, and the
///     first number of a new year adds its period entry atomically. Model: Invoice (<c>Number</c> from "InvoiceNumber"),
///     CreditNote (<c>Number</c> from "CreditNoteNumber") and LegacyDoc (<c>Number</c> from "LegacyNumber"), all String.
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class AutoIncrementFiscalYearTests(CkModelImportGuardFixture fixture)
{
    private const string NumberAttribute = "Number";
    private const string InvoiceFormat = "RE-{yyyy}-{counter:D5}";
    private const string Vienna = "Europe/Vienna";

    private static readonly RtCkId<CkTypeId> InvoiceTypeId = new("FiscalYearTest/Invoice");
    private static readonly RtCkId<CkTypeId> CreditNoteTypeId = new("FiscalYearTest/CreditNote");
    private static readonly RtCkId<CkTypeId> LegacyDocTypeId = new("FiscalYearTest/LegacyDoc");

    /// <summary>
    ///     AC 1: fiscal year = calendar year, format <c>RE-{yyyy}-{counter:D5}</c>: documents of 31 December 2026 get
    ///     <c>RE-2026-0000n</c>, the next one on 1 January 2027 gets <c>RE-2027-00001</c>. The 2027 document is created
    ///     at 00:30 Vienna time, still 31 December in UTC: the zone, not UTC, decides.
    /// </summary>
    [Fact]
    public async Task CalendarYear_RestartsTheCounterOnTheFirstOfJanuary()
    {
        await WithTenantAsync("fycal", async (dataSource, database) =>
        {
            var counterId = await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            var clock = new FakeClock(At("2026-12-31T10:00:00+01:00"));
            var modifier = NewModifier(clock);

            var first = Invoice();
            var second = Invoice();
            await RunAsync(modifier, dataSource, first, second);

            clock.Now = At("2027-01-01T00:30:00+01:00");
            var newYear = Invoice();
            await RunAsync(modifier, dataSource, newYear);

            Assert.Equal("RE-2026-00001", Number(first));
            Assert.Equal("RE-2026-00002", Number(second));
            Assert.Equal("RE-2027-00001", Number(newYear));
            Assert.Equal(new Dictionary<int, long> { { 2026, 2 }, { 2027, 1 } },
                await PeriodsAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 2: the fiscal year starts on 1 July; 30 June 2027 is in fiscal year 2026, 1 July 2027 starts the counter of
    ///     fiscal year 2027, and <c>{yyyy}</c> / <c>{yy}</c> show the fiscal year (named by the year it starts in, D4).
    /// </summary>
    [Fact]
    public async Task FiscalYearStartingInJuly_RestartsOnTheFirstOfJuly_AndTheTokenShowsTheFiscalYear()
    {
        await WithTenantAsync("fyjuly", async (dataSource, _) =>
        {
            await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat, startMonth: 7);
            await InsertCounterAsync(dataSource, "CreditNoteNumber", "GS{yy}-{counter:D3}", startMonth: 7);
            var clock = new FakeClock(At("2027-06-30T18:00:00+02:00"));
            var modifier = NewModifier(clock);

            var june = Invoice();
            var juneCredit = CreditNote();
            await RunAsync(modifier, dataSource, june, juneCredit);

            clock.Now = At("2027-07-01T08:00:00+02:00");
            var july = Invoice();
            var julyCredit = CreditNote();
            await RunAsync(modifier, dataSource, july, julyCredit);

            Assert.Equal("RE-2026-00001", Number(june));
            Assert.Equal("GS26-001", Number(juneCredit));
            Assert.Equal("RE-2027-00001", Number(july));
            Assert.Equal("GS27-001", Number(julyCredit));
        });
    }

    /// <summary>AC 3: two document types with their own ranges number independently, also within one batch.</summary>
    [Fact]
    public async Task TwoDocumentTypes_NumberIndependently()
    {
        await WithTenantAsync("fytypes", async (dataSource, database) =>
        {
            var invoiceCounter = await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            var creditCounter = await InsertCounterAsync(dataSource, "CreditNoteNumber", "GS-{yyyy}-{counter:D4}");
            var modifier = NewModifier(new FakeClock(At("2026-05-04T12:00:00+02:00")));

            var batch = new[] { Invoice(), CreditNote(), Invoice(), CreditNote(), Invoice() };
            await RunAsync(modifier, dataSource, batch);

            Assert.Equal(["RE-2026-00001", "GS-2026-0001", "RE-2026-00002", "GS-2026-0002", "RE-2026-00003"],
                batch.Select(Number));
            Assert.Equal(new Dictionary<int, long> { { 2026, 3 } }, await PeriodsAsync(database, invoiceCounter));
            Assert.Equal(new Dictionary<int, long> { { 2026, 2 } }, await PeriodsAsync(database, creditCounter));
        });
    }

    /// <summary>
    ///     AC 4 (regression): a counter with the legacy format <c>{0:D4}</c> and no period numbers exactly as before -
    ///     <c>CurrentValue</c> advances, no <c>Periods</c> appear, the clock plays no part.
    /// </summary>
    [Fact]
    public async Task LegacyFormat_WithoutPeriod_NumbersExactlyAsBefore()
    {
        await WithTenantAsync("fylegacy", async (dataSource, database) =>
        {
            var counterId = await InsertCounterAsync(dataSource, "LegacyNumber", "{0:D4}", periodKind: null,
                zone: null, currentValue: 41);
            var clock = new FakeClock(At("2026-12-31T23:59:59+01:00"));
            var modifier = NewModifier(clock);

            var first = LegacyDoc();
            await RunAsync(modifier, dataSource, first);
            clock.Now = At("2027-01-01T00:00:01+01:00");
            var second = LegacyDoc();
            await RunAsync(modifier, dataSource, second);

            Assert.Equal("0042", Number(first));
            Assert.Equal("0043", Number(second));
            var counter = (await FindDocumentAsync(database, counterId))["attributes"].AsBsonDocument;
            Assert.Equal(43, counter["currentValue"].ToInt64());
            Assert.False(counter.Contains("periods"));
        });
    }

    /// <summary>
    ///     AB#9757: the seed example of the octo-documentation page "Number ranges" (a fiscal-year invoice range,
    ///     <c>testData/numberRangeDocs/fiscal-year-invoice-range.yaml</c>, a byte-identical copy of the page's YAML block)
    ///     imports with ImportRt on the embedded System and numbers as the page says: <c>RE-2026-00001</c>,
    ///     <c>RE-2026-00002</c>, and <c>RE-2027-00001</c> after 1 January in Vienna time.
    /// </summary>
    [Fact]
    public async Task DocumentationSeedExample_Imports_AndNumbersPerFiscalYear()
    {
        await WithTenantAsync("fydocs", async (dataSource, database) =>
        {
            var repository = await GetRepositoryAsync(dataSource.TenantId);
            await ImportRtAsync(repository, Path.Combine("testData", "numberRangeDocs", "fiscal-year-invoice-range.yaml"));
            var counterId = new OctoObjectId("6750a1c2e4b0f1a2b3c4d5e6");
            var counter = await ReadCounterAsync(dataSource, counterId);
            Assert.Equal(RtAutoIncrementPeriodKindsEnum.FiscalYear, counter.PeriodKind);
            Assert.Equal(Vienna, counter.ReferenceTimeZone);

            var clock = new FakeClock(At("2026-12-31T10:00:00+01:00"));
            var modifier = NewModifier(clock);
            var first = Invoice();
            var second = Invoice();
            await RunAsync(modifier, dataSource, first, second);
            clock.Now = At("2027-01-01T00:30:00+01:00");
            var newYear = Invoice();
            await RunAsync(modifier, dataSource, newYear);

            Assert.Equal(["RE-2026-00001", "RE-2026-00002", "RE-2027-00001"],
                new[] { first, second, newYear }.Select(Number));
            Assert.Equal(new Dictionary<int, long> { { 2026, 2 }, { 2027, 1 } },
                await PeriodsAsync(database, counterId));
        });
    }

    /// <summary>
    ///     A batch that runs across the start of a fiscal year gives each entity the number of its own year: the clock
    ///     is read per entity and moves one second per read, from one second before midnight (Vienna).
    /// </summary>
    [Fact]
    public async Task BatchAcrossTheYearBoundary_NumbersEachEntityInItsOwnYear()
    {
        await WithTenantAsync("fyspan", async (dataSource, database) =>
        {
            var counterId = await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            var modifier = NewModifier(new FakeClock(At("2026-12-31T23:59:59+01:00"), TimeSpan.FromSeconds(1)));

            var batch = new[] { Invoice(), Invoice(), Invoice() };
            await RunAsync(modifier, dataSource, batch);

            Assert.Equal(["RE-2026-00001", "RE-2027-00001", "RE-2027-00002"], batch.Select(Number));
            Assert.Equal(new Dictionary<int, long> { { 2026, 1 }, { 2027, 2 } },
                await PeriodsAsync(database, counterId));
        });
    }

    /// <summary>The first number of a new fiscal year is the range's <c>StartValue</c> (D5); <c>End</c> bounds every year.</summary>
    [Fact]
    public async Task NewFiscalYear_StartsAtStartValue_AndEndBoundsEveryYear()
    {
        await WithTenantAsync("fystart", async (dataSource, _) =>
        {
            await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat, startValue: 100, end: 101);
            var modifier = NewModifier(new FakeClock(At("2026-03-01T12:00:00+01:00")));

            var first = Invoice();
            var second = Invoice();
            await RunAsync(modifier, dataSource, first, second);

            Assert.Equal("RE-2026-00100", Number(first));
            Assert.Equal("RE-2026-00101", Number(second));
            var exception = await Record.ExceptionAsync(() => RunAsync(modifier, dataSource, Invoice()));
            Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("fiscal year 2026", exception.Message);
        });
    }

    /// <summary>
    ///     AC 5: parallel creations across the year boundary get distinct numbers. 10 writers in 2026 (whose period
    ///     exists) and 10 in 2027 (whose period none of them finds), each in its own session and transaction; a writer
    ///     that loses a conflict is aborted with <c>TransientTransactionError</c> and runs again with its entity built
    ///     anew. The 2027 period is added exactly once.
    /// </summary>
    [Fact]
    public async Task ParallelCreationsAcrossTheYearBoundary_InTransactions_GetDistinctNumbers()
    {
        await ParallelAcrossTheYearBoundaryAsync("fyparx", inTransactions: true);
    }

    /// <summary>AC 5 without transactions: every statement is atomic on its own.</summary>
    [Fact]
    public async Task ParallelCreationsAcrossTheYearBoundary_WithoutTransactions_GetDistinctNumbers()
    {
        await ParallelAcrossTheYearBoundaryAsync("fyparnotx", inTransactions: false);
    }

    private async Task ParallelAcrossTheYearBoundaryAsync(string prefix, bool inTransactions)
    {
        await WithTenantAsync(prefix, async (dataSource, database) =>
        {
            var counterId = await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            var oldYear = NewModifier(new FakeClock(At("2026-12-31T23:59:59+01:00")));
            var newYear = NewModifier(new FakeClock(At("2027-01-01T00:00:01+01:00")));
            await RunAsync(oldYear, dataSource, Invoice()); // 2026 exists with 1, 2027 does not.

            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                CreateWithRetryAsync(i % 2 == 0 ? oldYear : newYear, dataSource, inTransactions))));

            var numbers2026 = results.Where(n => n.StartsWith("RE-2026-", StringComparison.Ordinal)).Order().ToList();
            var numbers2027 = results.Where(n => n.StartsWith("RE-2027-", StringComparison.Ordinal)).Order().ToList();
            Assert.Equal(Enumerable.Range(2, 10).Select(n => $"RE-2026-{n:D5}"), numbers2026);
            Assert.Equal(Enumerable.Range(1, 10).Select(n => $"RE-2027-{n:D5}"), numbers2027);

            var counter = (await FindDocumentAsync(database, counterId))["attributes"].AsBsonDocument;
            Assert.Equal(2, counter["periods"].AsBsonArray.Count);
            Assert.Equal(new Dictionary<int, long> { { 2026, 11 }, { 2027, 10 } },
                await PeriodsAsync(database, counterId));
        });
    }

    /// <summary>
    ///     A fiscal-year range is read back by the repository (typed) after the modifier added and advanced its
    ///     periods: the added period entry has the stored record shape.
    /// </summary>
    [Fact]
    public async Task AddedPeriods_ReadBackThroughTheRepository()
    {
        await WithTenantAsync("fyread", async (dataSource, _) =>
        {
            var counterId = await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            var clock = new FakeClock(At("2026-12-31T12:00:00+01:00"));
            var modifier = NewModifier(clock);
            await RunAsync(modifier, dataSource, Invoice());
            clock.Now = At("2027-01-02T12:00:00+01:00");
            await RunAsync(modifier, dataSource, Invoice(), Invoice());

            var counter = await ReadCounterAsync(dataSource, counterId);

            Assert.NotNull(counter.Periods);
            var periods = counter.Periods!.ToDictionary(p => Convert.ToInt32(p.FiscalYear),
                p => Convert.ToInt64(p.CurrentValue));
            Assert.Equal(new Dictionary<int, long> { { 2026, 1 }, { 2027, 2 } }, periods);
        });
    }

    /// <summary>
    ///     Through the repository (insert of the document, the service's own clock): a fiscal-year range numbers the
    ///     document in the current fiscal year of its zone.
    /// </summary>
    [Fact]
    public async Task RepositoryInsert_NumbersInTheCurrentFiscalYear()
    {
        await WithTenantAsync("fyrepo", async (dataSource, database) =>
        {
            await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            var repository = await GetRepositoryAsync(database.DatabaseNamespace.DatabaseName);
            var invoice = Invoice();
            var fiscalYear = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(Vienna)).Year;

            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(invoice));

            var stored = (await FindDocumentAsync(database, invoice.RtId))["attributes"]["number"].AsString;
            Assert.Equal($"RE-{fiscalYear}-00001", stored);
        });
    }

    /// <summary>
    ///     D2: a fiscal-year range without a known IANA zone is refused when it is configured (insert and replace),
    ///     with the reason; nothing is stored. No UTC default.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("W. Europe Standard Time")]
    public async Task ConfiguringAFiscalYearRange_WithoutAValidZone_IsRefused(string? zone)
    {
        await WithTenantAsync("fyzone", async (dataSource, database) =>
        {
            var repository = await GetRepositoryAsync(database.DatabaseNamespace.DatabaseName);
            var counter = await NewCounterAsync(repository, "InvoiceNumber", InvoiceFormat, zone: zone);

            var exception = await Record.ExceptionAsync(() =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(counter)));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("ReferenceTimeZone", refused.Message);
            Assert.Null(await TryFindDocumentAsync(database, counter.RtId));
        });
    }

    /// <summary>A valid range is accepted, and replacing it with one that drops the zone is refused.</summary>
    [Fact]
    public async Task ReplacingARange_WithoutTheZone_IsRefused()
    {
        await WithTenantAsync("fyreplace", async (dataSource, database) =>
        {
            var repository = await GetRepositoryAsync(database.DatabaseNamespace.DatabaseName);
            var counter = await NewCounterAsync(repository, "InvoiceNumber", InvoiceFormat, zone: Vienna);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(counter));

            var replacement = await NewCounterAsync(repository, "InvoiceNumber", InvoiceFormat, zone: null);
            replacement.RtId = counter.RtId;
            var exception = await Record.ExceptionAsync(() => ApplyAsync(repository,
                EntityUpdateInfo<RtEntity>.CreateReplace(counter.ToRtEntityId(), replacement)));

            Assert.IsType<AutoIncrementFailedException>(exception);
            var stored = (await FindDocumentAsync(database, counter.RtId))["attributes"].AsBsonDocument;
            Assert.Equal(Vienna, stored["referenceTimeZone"].AsString);
        });
    }

    /// <summary>The other configuration rules are checked when the range is configured, too.</summary>
    [Theory]
    [InlineData("RE-{yyyy}-{counter:D5}", null, "PeriodKind FiscalYear")]
    [InlineData("RE-{yyyy}-{0:D4}", 1, "unknown token")]
    [InlineData("RE-{counter:D5}", 13, "FiscalYearStartMonth")]
    public async Task ConfiguringAnInvalidRange_IsRefused(string format, int? periodKindOrMonth, string reason)
    {
        await WithTenantAsync("fyrules", async (dataSource, database) =>
        {
            var repository = await GetRepositoryAsync(database.DatabaseNamespace.DatabaseName);
            var counter = periodKindOrMonth == 13
                ? await NewCounterAsync(repository, "InvoiceNumber", format, startMonth: 13)
                : await NewCounterAsync(repository, "InvoiceNumber", format,
                    periodKind: periodKindOrMonth == null ? null : RtAutoIncrementPeriodKindsEnum.FiscalYear,
                    zone: periodKindOrMonth == null ? null : Vienna);

            var exception = await Record.ExceptionAsync(() =>
                ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(counter)));

            Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains(reason, exception.Message);
        });
    }

    /// <summary>
    ///     A range that reaches the database without the configuration check (here a direct write that removes the
    ///     zone) hands out no number: the allocation checks the rules again, and no default zone is used.
    /// </summary>
    [Fact]
    public async Task FiscalYearRange_WhoseZoneWasRemovedBehindTheValidator_HandsOutNoNumber()
    {
        await WithTenantAsync("fybypass", async (dataSource, database) =>
        {
            var counterId = await InsertCounterAsync(dataSource, "InvoiceNumber", InvoiceFormat);
            await database.GetCollection<BsonDocument>(CounterCollection).UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(counterId.ToString())),
                new BsonDocument("$unset", new BsonDocument("attributes.referenceTimeZone", "")),
                cancellationToken: TestContext.Current.CancellationToken);
            var invoice = Invoice();

            var exception = await Record.ExceptionAsync(() =>
                RunAsync(NewModifier(new FakeClock(At("2026-06-01T12:00:00+02:00"))), dataSource, invoice));

            Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("ReferenceTimeZone is required", exception.Message);
            Assert.Null(invoice.GetAttributeValueOrDefault(NumberAttribute));
            var counter = (await FindDocumentAsync(database, counterId))["attributes"].AsBsonDocument;
            Assert.False(counter.Contains("periods"));
        });
    }

    private const string CounterCollection = "RtEntity_SystemAutoIncrement";

    private AutoIncrementModifier NewModifier(TimeProvider clock) =>
        new(fixture.GetService<ICkCacheService>(), NullLogger<AutoIncrementModifier>.Instance, clock);

    private static async Task RunAsync(AutoIncrementModifier modifier, MongoDbRepositoryDataSource dataSource,
        params RtEntity[] entities)
    {
        using var session = await dataSource.GetSessionAsync();
        await modifier.RunAsync(session, dataSource, entities);
    }

    /// <summary>
    ///     One creation as a request handler does it: on <c>TransientTransactionError</c> the whole attempt runs again
    ///     with the entity built anew from the input (the aborted attempt's object carries a rolled-back number).
    /// </summary>
    private static async Task<string> CreateWithRetryAsync(AutoIncrementModifier modifier,
        MongoDbRepositoryDataSource dataSource, bool inTransaction)
    {
        const int maxAttempts = 100;
        for (var attempt = 1;; attempt++)
        {
            var invoice = Invoice();
            try
            {
                using var session = await dataSource.GetSessionAsync();
                if (inTransaction)
                {
                    session.StartTransaction();
                }

                await modifier.RunAsync(session, dataSource, [invoice]);
                if (inTransaction)
                {
                    await session.CommitTransactionAsync();
                }

                return Number(invoice);
            }
            catch (Exception ex) when (attempt < maxAttempts &&
                                       RepositoryDistributedLockService.IsTransientTransactionError(ex))
            {
                await Task.Delay(Random.Shared.Next(5, 50));
            }
        }
    }

    private async Task<OctoObjectId> InsertCounterAsync(MongoDbRepositoryDataSource dataSource, string name,
        string format, RtAutoIncrementPeriodKindsEnum? periodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear,
        string? zone = Vienna, int? startMonth = null, int? startValue = null, int end = 100000,
        int currentValue = 0)
    {
        var repository = await GetRepositoryAsync(dataSource.TenantId);
        var counter = await NewCounterAsync(repository, name, format, periodKind, zone, startMonth, startValue, end,
            currentValue);
        await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(counter));
        return counter.RtId;
    }

    private static async Task<RtAutoIncrement> NewCounterAsync(ITenantRepository repository, string name,
        string format, RtAutoIncrementPeriodKindsEnum? periodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear,
        string? zone = Vienna, int? startMonth = null, int? startValue = null, int end = 100000,
        int currentValue = 0)
    {
        var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
        counter.RtId = OctoObjectId.GenerateNewId();
        counter.RtWellKnownName = name;
        counter.Format = format;
        counter.CurrentValue = currentValue;
        counter.End = end;
        counter.PeriodKind = periodKind;
        counter.ReferenceTimeZone = zone;
        counter.FiscalYearStartMonth = startMonth;
        counter.StartValue = startValue;
        return counter;
    }

    private async Task<RtAutoIncrement> ReadCounterAsync(MongoDbRepositoryDataSource dataSource,
        OctoObjectId counterId)
    {
        var repository = await GetRepositoryAsync(dataSource.TenantId);
        using var session = await repository.GetSessionAsync();
        var counter = await repository.GetRtEntityByRtIdAsync<RtAutoIncrement>(session, counterId);
        Assert.NotNull(counter);
        return counter;
    }

    private async Task ImportRtAsync(ITenantRepository repository, string file)
    {
        await fixture.GetService<IImportRtModelCommand>().ImportAsync(repository, file,
            ExchangeMimeTypes.MimeTypeYaml, ImportStrategy.Upsert);
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

    private static RtEntity Invoice() => new(InvoiceTypeId, OctoObjectId.GenerateNewId(),
        new Dictionary<string, object?> { { "Title", "invoice" } });

    private static RtEntity CreditNote() => new(CreditNoteTypeId, OctoObjectId.GenerateNewId(),
        new Dictionary<string, object?> { { "Title", "credit note" } });

    private static RtEntity LegacyDoc() => new(LegacyDocTypeId, OctoObjectId.GenerateNewId(),
        new Dictionary<string, object?> { { "Title", "legacy" } });

    private static string Number(RtEntity entity) =>
        Assert.IsType<string>(entity.GetAttributeValueOrDefault(NumberAttribute));

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant);

    private static async Task<Dictionary<int, long>> PeriodsAsync(IMongoDatabase database, OctoObjectId counterId)
    {
        var counter = (await FindDocumentAsync(database, counterId))["attributes"].AsBsonDocument;
        return counter["periods"].AsBsonArray.Select(p => p["attributes"].AsBsonDocument)
            .ToDictionary(p => p["fiscalYear"].ToInt32(), p => p["currentValue"].ToInt64());
    }

    private static async Task<BsonDocument> FindDocumentAsync(IMongoDatabase database, OctoObjectId rtId) =>
        await TryFindDocumentAsync(database, rtId) ?? throw new InvalidOperationException($"Entity {rtId} not found");

    private static async Task<BsonDocument?> TryFindDocumentAsync(IMongoDatabase database, OctoObjectId rtId)
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

        return null;
    }

    private readonly Dictionary<string, ITenantRepository> _repositories = new();

    private Task<ITenantRepository> GetRepositoryAsync(string tenantId) =>
        Task.FromResult(_repositories[tenantId.ToLowerInvariant()]);

    /// <summary>A throwaway tenant with the Invoice/CreditNote/LegacyDoc model; counters are added by each test.</summary>
    private async Task WithTenantAsync(string prefix,
        Func<MongoDbRepositoryDataSource, IMongoDatabase, Task> body)
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

            await tenant.ImportCkModelAsync(DocumentModel(SystemCkIds.CkModelId));
            _repositories[tenantId.ToLowerInvariant()] = tenant.GetTenantRepository();

            var database = GetTenantDatabase(tenantId);
            var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
                fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(database.DatabaseNamespace.DatabaseName),
                database.DatabaseNamespace.DatabaseName, database.DatabaseNamespace.DatabaseName);
            await body(dataSource, database);
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

    /// <summary>Invoice, CreditNote and LegacyDoc, each with a String <c>Number</c> from its own counter.</summary>
    private static CkCompiledModelRoot DocumentModel(CkModelId system)
    {
        var id = new CkModelId("FiscalYearTest-1.0.0");

        CkCompiledTypeDto DocumentType(string typeName, string reference) => new()
        {
            TypeId = new CkTypeId($"{typeName}-1"), IsCollectionRoot = true,
            DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
            Attributes =
            [
                new CkTypeAttributeDto
                {
                    CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Title-1")), AttributeName = "Title",
                    IsOptional = true
                },
                new CkTypeAttributeDto
                {
                    CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("DocumentNumber-1")),
                    AttributeName = NumberAttribute, IsOptional = true, AutoIncrementReference = reference
                }
            ]
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "AB#9754 number ranges per document type and fiscal year",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                    { AttributeId = new CkAttributeId("DocumentNumber-1"), ValueType = AttributeValueTypesDto.String }
            ],
            Types =
            [
                DocumentType("Invoice", "InvoiceNumber"),
                DocumentType("CreditNote", "CreditNoteNumber"),
                DocumentType("LegacyDoc", "LegacyNumber")
            ]
        };
    }

    /// <summary>A clock under the test's control; it moves by <paramref name="step" /> after every read.</summary>
    private sealed class FakeClock(DateTimeOffset start, TimeSpan step = default) : TimeProvider
    {
        private readonly object _lock = new();
        private DateTimeOffset _now = start;

        public DateTimeOffset Now
        {
            set
            {
                lock (_lock)
                {
                    _now = value;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                var now = _now.ToUniversalTime();
                _now += step;
                return now;
            }
        }
    }
}
