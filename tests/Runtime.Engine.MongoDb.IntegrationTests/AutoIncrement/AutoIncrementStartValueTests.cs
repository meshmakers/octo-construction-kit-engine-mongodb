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
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
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
///     AB#9755 against a real MongoDB, in throwaway child tenants: a number range continues an external range from its
///     <c>StartValue</c>, and a start value below the counter never produces a duplicate (D5: the next number is
///     <c>max(counter, StartValue - 1) + 1</c>, a floor, never a reset); company tenants under one group tenant number
///     independently (the counter lives in each tenant's database); a seed re-apply keeps the counter
///     (<c>TenantOwned</c>); and a partial update that makes a range invalid is refused before anything is written.
///     Model: Invoice (<c>Number</c> from "InvoiceNumber", String).
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class AutoIncrementStartValueTests(CkModelImportGuardFixture fixture)
{
    private const string NumberAttribute = "Number";
    private const string Reference = "InvoiceNumber";
    private const string LegacyFormat = "{0:D4}";
    private const string FiscalFormat = "RE-{yyyy}-{counter:D5}";
    private const string BrokenLegacyFormat = "X-{counter:Dx}";
    private const string Vienna = "Europe/Vienna";
    private const string CounterCollection = "RtEntity_SystemAutoIncrement";

    private static readonly RtCkId<CkTypeId> InvoiceTypeId = new("StartValueTest/Invoice");

    /// <summary>
    ///     AC 1: a range with start value 1235 continues an external range that ended at 1234: the first document gets
    ///     1235, the next 1236, and a batch of two the two numbers after them.
    /// </summary>
    [Fact]
    public async Task StartValue_ContinuesAnExternalRange()
    {
        await WithTenantAsync("svcont", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, LegacyFormat, startValue: 1235);

            var first = await CreateInvoicesAsync(repository, 1);
            var second = await CreateInvoicesAsync(repository, 1);
            var batch = await CreateInvoicesAsync(repository, 2);

            Assert.Equal(["1235"], first);
            Assert.Equal(["1236"], second);
            Assert.Equal(["1237", "1238"], batch);
            Assert.Equal(1238, await StoredCurrentValueAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 2 (D5): a start value set below a counter already in use is accepted and ignored - the next number follows
    ///     the counter, never a number already issued; a start value above the counter continues from it; lowering it
    ///     again changes nothing.
    /// </summary>
    [Fact]
    public async Task StartValueBelowTheCounter_IsIgnored_AndNeverProducesADuplicate()
    {
        await WithTenantAsync("svbelow", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, LegacyFormat, currentValue: 50);
            var numbers = new List<string>();

            await UpdateCounterAsync(repository, counterId, c => c.StartValue = 10);
            numbers.AddRange(await CreateInvoicesAsync(repository, 2));
            await UpdateCounterAsync(repository, counterId, c => c.StartValue = 100);
            numbers.AddRange(await CreateInvoicesAsync(repository, 1));
            await UpdateCounterAsync(repository, counterId, c => c.StartValue = 5);
            numbers.AddRange(await CreateInvoicesAsync(repository, 1));

            Assert.Equal(["0051", "0052", "0100", "0101"], numbers);
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
            Assert.Equal(101, await StoredCurrentValueAsync(database, counterId));
        });
    }

    /// <summary>
    ///     D5 for a fiscal-year range whose year is already in use: a start value raised above the period's counter
    ///     continues from it, one set below it is ignored; the period is advanced in place (no second entry).
    /// </summary>
    [Fact]
    public async Task FiscalYearPeriodInUse_TakesTheStartValueAsAFloor()
    {
        await WithTenantAsync("svfiscal", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, FiscalFormat,
                periodKind: RtAutoIncrementPeriodKindsEnum.FiscalYear, zone: Vienna);
            var dataSource = DataSourceOf(database);
            var modifier = new AutoIncrementModifier(fixture.GetService<ICkCacheService>(),
                NullLogger<AutoIncrementModifier>.Instance, new FixedClock(DateTimeOffset.Parse("2026-05-04T10:00:00+02:00")));

            var before = await NumberWithModifierAsync(modifier, dataSource, 2);
            await UpdateCounterAsync(repository, counterId, c => c.StartValue = 1235);
            var raised = await NumberWithModifierAsync(modifier, dataSource, 2);
            await UpdateCounterAsync(repository, counterId, c => c.StartValue = 2);
            var lowered = await NumberWithModifierAsync(modifier, dataSource, 1);

            Assert.Equal(["RE-2026-00001", "RE-2026-00002"], before);
            Assert.Equal(["RE-2026-01235", "RE-2026-01236"], raised);
            Assert.Equal(["RE-2026-01237"], lowered);
            var periods = (await FindCounterAsync(database, counterId))["attributes"]["periods"].AsBsonArray;
            var period = Assert.Single(periods)["attributes"].AsBsonDocument;
            Assert.Equal(2026, period["fiscalYear"].ToInt32());
            Assert.Equal(1237, period["currentValue"].ToInt64());
        });
    }

    /// <summary>
    ///     AC 3: two company tenants under one group tenant, each with the same range configuration, each create three
    ///     documents: both get 1, 2, 3 of their own; the counters live in two tenant databases and neither changes the
    ///     other, and the group tenant has no counter at all.
    /// </summary>
    [Fact]
    public async Task CompanyTenantsUnderOneGroupTenant_NumberIndependently()
    {
        var systemContext = fixture.GetSystemContext();
        var groupId = $"svgroup{Guid.NewGuid():N}"[..20];
        var companyIds = new[] { $"svcoa{Guid.NewGuid():N}"[..20], $"svcob{Guid.NewGuid():N}"[..20] };
        ITenantContext? group = null;
        try
        {
            group = await CreateChildTenantAsync(systemContext, groupId);
            var companies = new List<ITenantContext>();
            foreach (var companyId in companyIds)
            {
                companies.Add(await CreateChildTenantAsync(group, companyId));
            }

            var counters = new List<(IMongoDatabase Database, OctoObjectId CounterId)>();
            foreach (var company in companies)
            {
                await company.ImportCkModelAsync(DocumentModel(SystemCkIds.CkModelId));
                var repository = company.GetTenantRepository();
                counters.Add((GetTenantDatabase(company.DatabaseName), await InsertCounterAsync(repository, LegacyFormat)));
            }

            // Interleaved, so a shared counter would hand out 1-6 across both.
            var numbers = companies.Select(_ => new List<string>()).ToList();
            for (var i = 0; i < 3; i++)
            {
                for (var c = 0; c < companies.Count; c++)
                {
                    numbers[c].AddRange(await CreateInvoicesAsync(companies[c].GetTenantRepository(), 1));
                }
            }

            Assert.All(numbers, n => Assert.Equal(["0001", "0002", "0003"], n));
            Assert.NotEqual(counters[0].Database.DatabaseNamespace.DatabaseName,
                counters[1].Database.DatabaseNamespace.DatabaseName);
            foreach (var (database, counterId) in counters)
            {
                Assert.Equal(3, await StoredCurrentValueAsync(database, counterId));
                Assert.Equal(1, await database.GetCollection<BsonDocument>(CounterCollection)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                        cancellationToken: TestContext.Current.CancellationToken));
            }

            var groupCounters = await GetTenantDatabase(group.DatabaseName).GetCollection<BsonDocument>(CounterCollection)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, groupCounters);
        }
        finally
        {
            if (group != null)
            {
                foreach (var companyId in companyIds)
                {
                    await DropChildTenantAsync(group, companyId);
                }
            }

            await ThrowawayTenant.DropAsync(systemContext, groupId);
        }
    }

    /// <summary>
    ///     AC 4: a re-apply of the seed that defines the ranges (an Upsert import, as a blueprint apply runs it) keeps the
    ///     counters - <c>CurrentValue</c> of a range without periods, <c>Periods</c> of a fiscal-year range - and a
    ///     start value the tenant changed (all <c>TenantOwned</c>); the range validator does not refuse the re-apply, and
    ///     numbering continues after it.
    /// </summary>
    [Fact]
    public async Task SeedReApply_KeepsTheCounters_AndIsNotRefused()
    {
        await WithTenantAsync("svseed", async (repository, database) =>
        {
            var legacyId = OctoObjectId.GenerateNewId();
            var fiscalId = OctoObjectId.GenerateNewId();
            var seed = Seed(legacyId, fiscalId);
            await ImportSeedAsync(repository, seed);

            var issued = await CreateInvoicesAsync(repository, 3);
            // One fixed instant for every fiscal-year allocation of the test, so it cannot run across a year start.
            const int fiscalYear = 2026;
            var dataSource = DataSourceOf(database);
            var modifier = new AutoIncrementModifier(fixture.GetService<ICkCacheService>(),
                NullLogger<AutoIncrementModifier>.Instance,
                new FixedClock(DateTimeOffset.Parse("2026-05-04T10:00:00+02:00")));
            var fiscalIssued = await NumberWithModifierAsync(modifier, dataSource, 2, "FiscalNumber");
            await UpdateCounterAsync(repository, legacyId, c => c.StartValue = 2000);

            await ImportSeedAsync(repository, seed);

            var legacy = (await FindCounterAsync(database, legacyId))["attributes"].AsBsonDocument;
            Assert.Equal(1237, legacy["currentValue"].ToInt64());
            Assert.Equal(2000, legacy["startValue"].ToInt64());
            var fiscal = (await FindCounterAsync(database, fiscalId))["attributes"].AsBsonDocument;
            var period = Assert.Single(fiscal["periods"].AsBsonArray)["attributes"].AsBsonDocument;
            Assert.Equal(fiscalYear, period["fiscalYear"].ToInt32());
            Assert.Equal(2, period["currentValue"].ToInt64());

            Assert.Equal(["1235", "1236", "1237"], issued);
            Assert.Equal([$"F-{fiscalYear}-0001", $"F-{fiscalYear}-0002"], fiscalIssued);
            Assert.Equal(["2000"], await CreateInvoicesAsync(repository, 1));
            Assert.Equal([$"F-{fiscalYear}-0003"], await NumberWithModifierAsync(modifier, dataSource, 1, "FiscalNumber"));
        });
    }

    /// <summary>
    ///     Gap of AB#9754, closed with AB#9755: a partial update that makes a range invalid is refused when it is written,
    ///     not only when the next number is allocated; the stored range stays unchanged.
    /// </summary>
    [Theory]
    [InlineData("periodWithoutZone", "ReferenceTimeZone is required")]
    [InlineData("unknownZone", "not a known IANA time zone")]
    [InlineData("yearTokenWithoutPeriod", "needs PeriodKind")]
    [InlineData("startValueZero", "StartValue 0 is below 1")]
    [InlineData("startValueAboveEnd", "StartValue 100001 is above End 100000")]
    [InlineData("endBelowStartValue", "StartValue 50 is above End 10")]
    [InlineData("legacyFormatThatCannotFormat", "Number range 'InvoiceNumber'")]
    [InlineData("legacyFormatThatCannotFormat", "Format 'X-{counter:Dx}' is not valid")]
    public async Task PartialUpdate_ThatMakesTheRangeInvalid_IsRefused(string change, string reason)
    {
        await WithTenantAsync("svpartial", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, LegacyFormat,
                startValue: change == "endBelowStartValue" ? 50 : null);
            var before = (await FindCounterAsync(database, counterId))["attributes"].AsBsonDocument;

            var exception = await Record.ExceptionAsync(() => UpdateCounterAsync(repository, counterId, c =>
            {
                switch (change)
                {
                    case "periodWithoutZone":
                        c.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
                        break;
                    case "unknownZone":
                        c.ReferenceTimeZone = "Mars/Olympus_Mons";
                        break;
                    case "yearTokenWithoutPeriod":
                        c.Format = FiscalFormat;
                        break;
                    case "startValueZero":
                        c.StartValue = 0;
                        break;
                    case "startValueAboveEnd":
                        c.StartValue = 100001;
                        break;
                    case "endBelowStartValue":
                        c.End = 10;
                        break;
                    case "legacyFormatThatCannotFormat":
                        c.Format = BrokenLegacyFormat;
                        break;
                }
            }));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains(reason, refused.Message);
            Assert.Equal(before, (await FindCounterAsync(database, counterId))["attributes"].AsBsonDocument);
        });
    }

    /// <summary>
    ///     A partial update that only touches the counter or makes a valid change is written; setting the zone and the
    ///     period kind together in one update is valid although each alone on a range without a zone would not be.
    /// </summary>
    [Fact]
    public async Task PartialUpdate_ThatKeepsTheRangeValid_IsWritten()
    {
        await WithTenantAsync("svpartok", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, LegacyFormat);

            await UpdateCounterAsync(repository, counterId, c => c.CurrentValue = 7);
            await UpdateCounterAsync(repository, counterId, c =>
            {
                c.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
                c.ReferenceTimeZone = Vienna;
                c.Format = FiscalFormat;
                c.StartValue = 8;
            });

            var stored = (await FindCounterAsync(database, counterId))["attributes"].AsBsonDocument;
            Assert.Equal(7, stored["currentValue"].ToInt64());
            Assert.Equal((long)RtAutoIncrementPeriodKindsEnum.FiscalYear, stored["periodKind"].ToInt64());
            Assert.Equal(Vienna, stored["referenceTimeZone"].AsString);
            Assert.Equal(FiscalFormat, stored["format"].AsString);
            Assert.Equal(8, stored["startValue"].ToInt64());
        });
    }

    /// <summary>
    ///     The numbers left in the end-reached error count from the StartValue floor: counter 0, StartValue 100,
    ///     End 100 leaves one number, not 100.
    /// </summary>
    [Fact]
    public async Task EndReached_CountsTheNumbersLeft_FromTheStartValue()
    {
        await WithTenantAsync("svleft", async (repository, database) =>
        {
            await InsertCounterAsync(repository, LegacyFormat, startValue: 100, end: 100);
            var modifier = new AutoIncrementModifier(fixture.GetService<ICkCacheService>(),
                NullLogger<AutoIncrementModifier>.Instance);

            var exception = await Record.ExceptionAsync(() =>
                NumberWithModifierAsync(modifier, DataSourceOf(database), 2));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("needs 2 number(s), 1 left up to End 100", refused.Message);
            Assert.Equal(["0100"], await NumberWithModifierAsync(modifier, DataSourceOf(database), 1));
        });
    }

    /// <summary>
    ///     A range in use without periods (counter at 7) that is switched to FiscalYear without a StartValue above 7
    ///     would start the current fiscal year at 1 and issue 1-7 again, e.g. '26-{0:D4}' becoming
    ///     '{yy}-{counter:D4}'. The switch is refused, by a partial update and by a replace, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 7)]
    [InlineData(true, null)]
    [InlineData(true, 5)]
    public async Task SwitchingARangeInUse_ToFiscalYear_WithoutAStartValueAboveTheCounter_IsRefused(bool replace,
        int? startValue)
    {
        await WithTenantAsync("svswitch", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, "26-{0:D4}", currentValue: 7);

            Exception? exception;
            if (replace)
            {
                var replacement = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
                replacement.RtId = counterId;
                replacement.RtWellKnownName = Reference;
                replacement.Format = "{yy}-{counter:D4}";
                replacement.CurrentValue = 7;
                replacement.End = 100000;
                replacement.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
                replacement.ReferenceTimeZone = Vienna;
                replacement.StartValue = startValue;
                exception = await Record.ExceptionAsync(() => ApplyAsync(repository,
                    EntityUpdateInfo<RtEntity>.CreateReplace(replacement.ToRtEntityId(), replacement)));
            }
            else
            {
                exception = await Record.ExceptionAsync(() => UpdateCounterAsync(repository, counterId, c =>
                {
                    c.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
                    c.ReferenceTimeZone = Vienna;
                    c.Format = "{yy}-{counter:D4}";
                    c.StartValue = startValue;
                }));
            }

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains($"Number range '{Reference}'", refused.Message);
            Assert.Contains("in use without fiscal-year periods (CurrentValue 7)", refused.Message);
            var stored = (await FindCounterAsync(database, counterId))["attributes"].AsBsonDocument;
            Assert.Equal("26-{0:D4}", stored["format"].AsString);
            Assert.False(stored.Contains("periodKind") && !stored["periodKind"].IsBsonNull &&
                         stored["periodKind"].ToInt64() == (long)RtAutoIncrementPeriodKindsEnum.FiscalYear);
            Assert.Equal(7, stored["currentValue"].ToInt64());
        });
    }

    /// <summary>
    ///     AB#9755: a range configured with a legacy format that string.Format cannot format is refused when it is
    ///     inserted, not at the first allocation; the error names the range and the format, and nothing is stored.
    /// </summary>
    [Fact]
    public async Task Insert_WithALegacyFormatThatCannotFormat_IsRefused_NamingTheRange()
    {
        await WithTenantAsync("svbadfmt", async (repository, database) =>
        {
            var exception = await Record.ExceptionAsync(() => InsertCounterAsync(repository, BrokenLegacyFormat));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains($"Number range '{Reference}'", refused.Message);
            Assert.Contains($"Format '{BrokenLegacyFormat}' is not valid", refused.Message);
            Assert.Equal(0, await database.GetCollection<BsonDocument>(CounterCollection)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken));
        });
    }

    /// <summary>
    ///     AB#9755: a range whose legacy format string.Format cannot format reached the database without the check (a
    ///     format stored before System 2.9.0; here a direct write) is refused at allocation with an error that names the
    ///     range and the format. The counter is never set back (another request
    ///     may have taken the next block meanwhile): the refused block stays unused, a gap, never a duplicate - once the
    ///     format is fixed the next number follows the refused block.
    /// </summary>
    [Fact]
    public async Task Allocation_WithAStoredLegacyFormatThatCannotFormat_IsRefused_NamingTheRange_NeverADuplicate()
    {
        await WithTenantAsync("svbadalloc", async (repository, database) =>
        {
            var counterId = await InsertCounterAsync(repository, LegacyFormat, currentValue: 40);
            await SetStoredFormatAsync(database, counterId, BrokenLegacyFormat);
            var modifier = new AutoIncrementModifier(fixture.GetService<ICkCacheService>(),
                NullLogger<AutoIncrementModifier>.Instance, TimeProvider.System);
            var dataSource = DataSourceOf(database);

            var exception = await Record.ExceptionAsync(() => NumberWithModifierAsync(modifier, dataSource, 2));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains($"Number range '{Reference}'", refused.Message);
            Assert.Contains($"Format '{BrokenLegacyFormat}' is not valid", refused.Message);
            Assert.Equal(42, await StoredCurrentValueAsync(database, counterId));

            await SetStoredFormatAsync(database, counterId, LegacyFormat);
            Assert.Equal(["0043"], await NumberWithModifierAsync(modifier, dataSource, 1));
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static async Task<OctoObjectId> InsertCounterAsync(ITenantRepository repository, string format,
        RtAutoIncrementPeriodKindsEnum? periodKind = null, string? zone = null, int? startValue = null,
        int end = 100000, int currentValue = 0, string reference = Reference)
    {
        var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
        counter.RtId = OctoObjectId.GenerateNewId();
        counter.RtWellKnownName = reference;
        counter.Format = format;
        counter.CurrentValue = currentValue;
        counter.End = end;
        counter.PeriodKind = periodKind;
        counter.ReferenceTimeZone = zone;
        counter.StartValue = startValue;
        await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateInsert(counter));
        return counter.RtId;
    }

    /// <summary>A partial update of the counter: only the attributes <paramref name="change" /> sets are written.</summary>
    private static async Task UpdateCounterAsync(ITenantRepository repository, OctoObjectId counterId,
        Action<RtAutoIncrement> change)
    {
        var partial = new RtAutoIncrement { RtId = counterId };
        change(partial);
        await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(partial.ToRtEntityId(), partial));
    }

    /// <summary>Creates <paramref name="count" /> invoices in one batch through the repository; returns their numbers.</summary>
    private static async Task<List<string>> CreateInvoicesAsync(ITenantRepository repository, int count)
    {
        var invoices = Enumerable.Range(0, count).Select(_ => Invoice()).ToList();
        await ApplyAsync(repository, invoices.Select(i => (IEntityUpdateInfo<RtEntity>)EntityUpdateInfo<RtEntity>
            .CreateInsert(i)).ToArray());
        return invoices.Select(Number).ToList();
    }

    /// <summary>Numbers <paramref name="count" /> invoices of counter <paramref name="reference" /> with the modifier.</summary>
    private static async Task<List<string>> NumberWithModifierAsync(AutoIncrementModifier modifier,
        MongoDbRepositoryDataSource dataSource, int count, string reference = Reference)
    {
        var invoices = Enumerable.Range(0, count)
            .Select(_ => reference == Reference ? Invoice() : FiscalInvoice()).ToList();
        using var session = await dataSource.GetSessionAsync();
        await modifier.RunAsync(session, dataSource, invoices);
        return invoices.Select(Number).ToList();
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

    private static RtEntity FiscalInvoice() => new(new RtCkId<CkTypeId>("StartValueTest/FiscalInvoice"),
        OctoObjectId.GenerateNewId(), new Dictionary<string, object?> { { "Title", "fiscal invoice" } });

    private static string Number(RtEntity entity) =>
        Assert.IsType<string>(entity.GetAttributeValueOrDefault(NumberAttribute));

    /// <summary>Writes the format straight into the collection, past the range validator.</summary>
    private static async Task SetStoredFormatAsync(IMongoDatabase database, OctoObjectId counterId, string format) =>
        await database.GetCollection<BsonDocument>(CounterCollection).UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(counterId.ToString())),
            new BsonDocument("$set", new BsonDocument("attributes.format", format)),
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<long> StoredCurrentValueAsync(IMongoDatabase database, OctoObjectId counterId) =>
        (await FindCounterAsync(database, counterId))["attributes"]["currentValue"].ToInt64();

    private static async Task<BsonDocument> FindCounterAsync(IMongoDatabase database, OctoObjectId counterId) =>
        await database.GetCollection<BsonDocument>(CounterCollection)
            .Find(new BsonDocument("_id", ObjectId.Parse(counterId.ToString())))
            .SingleAsync(TestContext.Current.CancellationToken);

    /// <summary>
    ///     The seed: a range without periods (start value 1235, <c>{0:D4}</c>) and a calendar fiscal-year range
    ///     (<c>F-{yyyy}-{counter:D4}</c>, Vienna), both with <c>CurrentValue</c> 0 as a blueprint seeds them.
    /// </summary>
    private static string Seed(OctoObjectId legacyId, OctoObjectId fiscalId)
    {
        static string Counter(OctoObjectId rtId, string name, params (string Attribute, string Value)[] attributes) =>
            $"  - rtId: {rtId}\n    ckTypeId: System/AutoIncrement\n    rtWellKnownName: {name}\n" +
            "    associations: []\n    attributes:\n" +
            string.Concat(attributes.Select(a =>
                $"      - id: System/AutoIncrement.{a.Attribute}\n        value: {a.Value}\n"));

        return $"$schema: {RtModelRootTcDto.RtSchemaUri}\n" +
               "dependencies:\n  - System-[2.0,3.0)\n" +
               "entities:\n" +
               Counter(legacyId, Reference, ("Format", "'{0:D4}'"), ("CurrentValue", "0"), ("End", "100000"),
                   ("StartValue", "1235")) +
               Counter(fiscalId, "FiscalNumber", ("Format", "'F-{yyyy}-{counter:D4}'"), ("CurrentValue", "0"),
                   ("End", "100000"), ("PeriodKind", "FiscalYear"), ("ReferenceTimeZone", Vienna));
    }

    private async Task ImportSeedAsync(ITenantRepository repository, string yaml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ab9755-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(file, yaml, TestContext.Current.CancellationToken);
        try
        {
            await fixture.GetService<IImportRtModelCommand>().ImportAsync(repository, file,
                ExchangeMimeTypes.MimeTypeYaml, ImportStrategy.Upsert);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private MongoDbRepositoryDataSource DataSourceOf(IMongoDatabase database) =>
        new(NullLogger<MongoDbRepositoryDataSource>.Instance,
            fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(database.DatabaseNamespace.DatabaseName),
            database.DatabaseNamespace.DatabaseName, database.DatabaseNamespace.DatabaseName);

    /// <summary>A throwaway child tenant of the system tenant with the Invoice model.</summary>
    private async Task WithTenantAsync(string prefix, Func<ITenantRepository, IMongoDatabase, Task> body)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        try
        {
            var tenant = await CreateChildTenantAsync(systemContext, tenantId);
            await tenant.ImportCkModelAsync(DocumentModel(SystemCkIds.CkModelId));
            await body(tenant.GetTenantRepository(), GetTenantDatabase(tenant.DatabaseName));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private static async Task<ITenantContext> CreateChildTenantAsync(ITenantContext parent, string tenantId)
    {
        using (var session = await parent.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await parent.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        using (var session = await parent.GetAdminSessionAsync())
        {
            session.StartTransaction();
            var child = await parent.GetChildTenantContextAsync(session, tenantId);
            await session.CommitTransactionAsync();
            return child;
        }
    }

    private static async Task DropChildTenantAsync(ITenantContext parent, string tenantId)
    {
        try
        {
            using var session = await parent.GetAdminSessionAsync();
            session.StartTransaction();
            await parent.DropChildTenantAsync(session, tenantId, dropStreamData: true);
            await session.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            // Best effort, like ThrowawayTenant: the test's own verdict stays the result.
            TestContext.Current.SendDiagnosticMessage($"clean up company tenant '{tenantId}' failed: {ex.Message}");
        }
    }

    private IMongoDatabase GetTenantDatabase(string databaseName)
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
        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName.ToLowerInvariant());
    }

    /// <summary>Invoice (<c>Number</c> from "InvoiceNumber") and FiscalInvoice (<c>Number</c> from "FiscalNumber").</summary>
    private static CkCompiledModelRoot DocumentModel(CkModelId system)
    {
        var id = new CkModelId("StartValueTest-1.0.0");

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
            Description = "AB#9755 start value and company tenants",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                    { AttributeId = new CkAttributeId("DocumentNumber-1"), ValueType = AttributeValueTypesDto.String }
            ],
            Types = [DocumentType("Invoice", Reference), DocumentType("FiscalInvoice", "FiscalNumber")]
        };
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
