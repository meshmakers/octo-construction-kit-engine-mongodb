using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     The gap report of a number range per fiscal year (<see cref="ITenantRepository.GetNumberRangeGapReportAsync" />)
///     against a real MongoDB, in throwaway child tenants. Model: <c>GapReportTest/Invoice</c> with the enum
///     <c>DocumentState</c> (Draft = 0, Released = 1, Cancelled = 2) and a String <c>Number</c> from the fiscal-year
///     range "GapInvoiceNumber" (<c>RE-{yyyy}-{counter:D5}</c>, released values Released and Cancelled); the type
///     declares the index on <c>Number</c>, as a document model must. <c>GapReportTest/Note</c> has an Int
///     <c>Number</c> from the range "GapNoteNumber" without periods and without release configuration.
///     Documents and counters of a fiscal year that has not begun yet are seeded straight into the collections (the
///     allocator numbers only the current fiscal year); one test numbers through the allocator.
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class NumberRangeGapReportTests(CkModelImportGuardFixture fixture)
{
    private const string InvoiceRange = "GapInvoiceNumber";
    private const string NoteRange = "GapNoteNumber";
    private const string InvoiceCollection = "RtEntity_GapReportTestInvoice";
    private const string Vienna = "Europe/Vienna";
    private const int Draft = 0;
    private const int Released = 1;
    private const int Cancelled = 2;

    private static readonly RtCkId<CkTypeId> InvoiceTypeId = new("GapReportTest/Invoice");
    private static readonly RtCkId<CkTypeId> NoteTypeId = new("GapReportTest/Note");

    /// <summary>A range with released numbers 1..10 for fiscal year 2027 lists no missing and no duplicate number.</summary>
    [Fact]
    public async Task CleanRange_ListsNoMissingAndNoDuplicateNumber()
    {
        await WithTenantAsync("gapclean", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 10));
            for (var i = 1; i <= 10; i++)
            {
                await SeedInvoiceAsync(database, Number(2027, i), i % 2 == 0 ? Cancelled : Released);
            }

            var report = await ReportAsync(repository, InvoiceRange, 2027);

            Assert.Equal(2027, report.FiscalYear);
            Assert.Equal(1, report.StartValue);
            Assert.Equal(10, report.CurrentValue);
            Assert.Equal(10, report.DocumentCount);
            Assert.Empty(report.Missing);
            Assert.Empty(report.Duplicates);
            Assert.Empty(report.Irregular);
        });
    }

    /// <summary>
    ///     A seeded gap (4 missing) and a seeded duplicate (7 twice, inserted bypassing the allocator): 4 is listed as
    ///     missing and 7 as duplicate with both document ids.
    /// </summary>
    [Fact]
    public async Task SeededGapAndDuplicate_AreListed_WithBothDocumentIds()
    {
        await WithTenantAsync("gapseed", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 10));
            var seven = new List<OctoObjectId>();
            for (var i = 1; i <= 10; i++)
            {
                if (i == 4)
                {
                    continue;
                }

                var id = await SeedInvoiceAsync(database, Number(2027, i), Released);
                if (i == 7)
                {
                    seven.Add(id);
                }
            }

            seven.Add(await SeedInvoiceAsync(database, Number(2027, 7), Released));

            var report = await ReportAsync(repository, InvoiceRange, 2027);

            var gap = Assert.Single(report.Missing);
            Assert.Equal((4, 4, "RE-2027-00004"), (gap.From, gap.To, gap.FromNumber));
            var duplicate = Assert.Single(report.Duplicates);
            Assert.Equal(7, duplicate.SequenceValue);
            Assert.Equal("RE-2027-00007", duplicate.Number);
            Assert.Equal(seven.Select(s => s.ToString()).Order(StringComparer.Ordinal),
                duplicate.Documents.Select(d => d.RtId.ToString()).Order(StringComparer.Ordinal));
            Assert.All(duplicate.Documents, d => Assert.Equal(InvoiceTypeId.FullName, d.CkTypeId.FullName));
            Assert.Equal(10, report.DocumentCount);
            Assert.Empty(report.Irregular);
        });
    }

    /// <summary>Two fiscal years: the report is per fiscal year and does not mix them.</summary>
    [Fact]
    public async Task TwoFiscalYears_AreReportedSeparately()
    {
        await WithTenantAsync("gapyears", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 3), (2028, 2));
            foreach (var i in new[] { 1, 2, 3 })
            {
                await SeedInvoiceAsync(database, Number(2027, i), Released);
            }

            await SeedInvoiceAsync(database, Number(2028, 1), Released);

            var year2027 = await ReportAsync(repository, InvoiceRange, 2027);
            var year2028 = await ReportAsync(repository, InvoiceRange, 2028);

            Assert.Equal((3, 3L), (year2027.DocumentCount, year2027.CurrentValue));
            Assert.Empty(year2027.Missing);
            Assert.Empty(year2027.Duplicates);
            Assert.Equal((1, 2L), (year2028.DocumentCount, year2028.CurrentValue));
            var gap = Assert.Single(year2028.Missing);
            Assert.Equal((2, 2, "RE-2028-00002"), (gap.From, gap.To, gap.FromNumber));
            Assert.Empty(year2028.Duplicates);

            // A fiscal year without a period has handed out nothing: nothing is missing.
            var year2029 = await ReportAsync(repository, InvoiceRange, 2029);
            Assert.Equal((0, 0L), (year2029.DocumentCount, year2029.CurrentValue));
            Assert.Empty(year2029.Missing);
        });
    }

    /// <summary>Drafts without a number are not counted as gaps; a document whose number is not released is not read.</summary>
    [Fact]
    public async Task Drafts_AreNotCounted()
    {
        await WithTenantAsync("gapdraft", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 3));
            foreach (var i in new[] { 1, 2, 3 })
            {
                await SeedInvoiceAsync(database, Number(2027, i), Released);
            }

            await SeedInvoiceAsync(database, null, Draft);
            await SeedInvoiceAsync(database, null, Draft);
            // A draft carrying a number (seeded; the allocator never writes one) is not a released document.
            await SeedInvoiceAsync(database, Number(2027, 2), Draft);

            var report = await ReportAsync(repository, InvoiceRange, 2027);

            Assert.Equal(3, report.DocumentCount);
            Assert.Empty(report.Missing);
            Assert.Empty(report.Duplicates);
            Assert.Empty(report.Irregular);
        });
    }

    /// <summary>Numbers outside the interval and numbers without the range's format are listed as irregular.</summary>
    [Fact]
    public async Task NumbersOutsideTheIntervalOrFormat_AreIrregular()
    {
        await WithTenantAsync("gapirreg", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 2));
            await SeedInvoiceAsync(database, Number(2027, 1), Released);
            await SeedInvoiceAsync(database, Number(2027, 2), Released);
            var above = await SeedInvoiceAsync(database, Number(2027, 5), Released);
            var padded = await SeedInvoiceAsync(database, "RE-2027-0001", Released);

            var report = await ReportAsync(repository, InvoiceRange, 2027);

            Assert.Empty(report.Missing);
            Assert.Equal(2, report.IrregularCount);
            Assert.Contains(report.Irregular, i => i.Document.RtId == above && i.SequenceValue == 5);
            Assert.Contains(report.Irregular, i => i.Document.RtId == padded && i.SequenceValue == null &&
                                                   i.Number == "RE-2027-0001");
        });
    }

    /// <summary>
    ///     The documents are read through the index the document model declares on the number attribute: the winning
    ///     plan of the report's aggregation is an IXSCAN of that index, bounded to the fiscal year's prefix, without a
    ///     COLLSCAN. The plan is written to the test output as the evidence.
    /// </summary>
    [Fact]
    public async Task Report_ReadsThroughTheIndexOnTheNumberAttribute()
    {
        await WithTenantAsync("gapindex", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 3));
            for (var i = 1; i <= 3; i++)
            {
                await SeedInvoiceAsync(database, Number(2027, i), Released);
            }

            var indexes = await (await database.GetCollection<BsonDocument>(InvoiceCollection).Indexes
                .ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("attributes.number"));

            var tenantId = database.DatabaseNamespace.DatabaseName;
            var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
                fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(tenantId), tenantId, tenantId);
            var ckCache = fixture.GetService<ICkCacheService>();
            var query = new NumberRangeGapReportQuery(repository.TenantId, ckCache, dataSource,
                _ => Task.FromResult<Contracts.DataPermissions.RtDataSecurityQueryFilter?>(null));
            var source = Assert.Single(query.GetSources(InvoiceRange, await ReleaseOfTheInvoiceRangeAsync(repository)));
            Assert.True(AutoIncrementNumberFormat.TryParse("RE-{yyyy}-{counter:D5}", out var format, out _));
            var filter = await query.BuildFilterAsync(source, format.CreateReader(2027), null);
            var pipeline = NumberRangeGapReportQuery.BuildPipeline(source, filter)
                .Render(new RenderArgs<RtEntity>(source.Collection.DocumentSerializer,
                    source.Collection.Settings.SerializerRegistry));

            var explain = await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                {
                    "explain", new BsonDocument
                    {
                        { "aggregate", InvoiceCollection }, { "pipeline", new BsonArray(pipeline.Documents) },
                        { "cursor", new BsonDocument() }
                    }
                },
                { "verbosity", "queryPlanner" }
            }, cancellationToken: TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(explain.ToJson(new JsonWriterSettings { Indent = true }));

            var stages = Stages(explain).ToList();
            Assert.DoesNotContain(stages, s => s.GetValue("stage", "").AsString == "COLLSCAN");
            var scan = Assert.Single(stages, s => s.GetValue("stage", "").AsString == "IXSCAN");
            Assert.True(scan["keyPattern"].AsBsonDocument.Contains("attributes.number"), scan.ToJson());
            var bounds = scan["indexBounds"]["attributes.number"].AsBsonArray.Select(b => b.AsString).ToList();
            Assert.Contains(bounds, b => b.StartsWith("[\"RE-2027-\"", StringComparison.Ordinal));
        });
    }

    /// <summary>Numbers handed out by the allocator on release give a clean report for the current fiscal year.</summary>
    [Fact]
    public async Task NumbersOfTheAllocator_GiveACleanReport()
    {
        await WithTenantAsync("gapalloc", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database);
            await ApplyAsync(repository, Insert(Invoice(Released)), Insert(Invoice(Released)), Insert(Invoice(Draft)),
                Insert(Invoice(Released)));
            var year = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Vienna))
                .Year;

            var report = await ReportAsync(repository, InvoiceRange, year);

            Assert.Equal((3, 1L, 3L), (report.DocumentCount, report.StartValue, report.CurrentValue));
            Assert.Empty(report.Missing);
            Assert.Empty(report.Duplicates);
            Assert.Empty(report.Irregular);
        });
    }

    /// <summary>
    ///     A range without periods (Int numbers, numbered on insert) is reported without a fiscal year; a fiscal year is
    ///     refused for it, and a fiscal-year range refuses a report without one. An unknown range answers null.
    /// </summary>
    [Fact]
    public async Task RangeWithoutPeriods_IsReportedWithoutAFiscalYear()
    {
        await WithTenantAsync("gapnote", async (repository, database) =>
        {
            await InsertInvoiceRangeAsync(repository, database, (2027, 1));
            var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
            counter.RtId = OctoObjectId.GenerateNewId();
            counter.RtWellKnownName = NoteRange;
            counter.CurrentValue = 0;
            counter.End = 99999;
            await ApplyAsync(repository, Insert(counter));
            var notes = Enumerable.Range(0, 4).Select(_ => new RtEntity(NoteTypeId, OctoObjectId.GenerateNewId(),
                new Dictionary<string, object?> { ["Title"] = "note" })).ToList();
            await ApplyAsync(repository, notes.Select(Insert).ToArray());
            // The third note is gone from the collection: its number 3 is missing.
            await database.GetCollection<BsonDocument>("RtEntity_GapReportTestNote").DeleteOneAsync(
                new BsonDocument("attributes.number", new BsonDocument("$in", new BsonArray { 3, 3L })),
                TestContext.Current.CancellationToken);

            var report = await ReportAsync(repository, NoteRange, null);

            Assert.Null(report.FiscalYear);
            Assert.Equal((3, 4L), (report.DocumentCount, report.CurrentValue));
            var gap = Assert.Single(report.Missing);
            Assert.Equal((3, 3, "3"), (gap.From, gap.To, gap.FromNumber));

            using var session = await repository.GetSessionAsync();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.GetNumberRangeGapReportAsync(session, NoteRange, 2027,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.GetNumberRangeGapReportAsync(session, InvoiceRange, null,
                    TestContext.Current.CancellationToken));
            Assert.Null(await repository.GetNumberRangeGapReportAsync(session, "NoSuchRange", null,
                TestContext.Current.CancellationToken));
        });
    }

    private static IEnumerable<BsonDocument> Stages(BsonValue node)
    {
        switch (node)
        {
            case BsonDocument document:
                if (document.Contains("stage"))
                {
                    yield return document;
                }

                foreach (var element in document.Elements.Where(e => e.Name != "rejectedPlans"))
                {
                    foreach (var stage in Stages(element.Value))
                    {
                        yield return stage;
                    }
                }

                break;
            case BsonArray array:
                foreach (var stage in array.SelectMany(Stages))
                {
                    yield return stage;
                }

                break;
        }
    }

    private static async Task<NumberRangeRelease> ReleaseOfTheInvoiceRangeAsync(ITenantRepository repository)
    {
        var counter = await NewInvoiceRangeAsync(repository);
        Assert.True(NumberRangeRelease.TryCreate(counter, out var release, out var error), error);
        return release!;
    }

    private static string Number(int fiscalYear, long value) => $"RE-{fiscalYear:D4}-{value:D5}";

    private static async Task<NumberRangeGapReport> ReportAsync(ITenantRepository repository, string range,
        int? fiscalYear)
    {
        using var session = await repository.GetSessionAsync();
        var report = await repository.GetNumberRangeGapReportAsync(session, range, fiscalYear,
            TestContext.Current.CancellationToken);
        Assert.NotNull(report);
        return report;
    }

    /// <summary>The fiscal-year range of the invoices, with the given period counters.</summary>
    private static async Task InsertInvoiceRangeAsync(ITenantRepository repository, IMongoDatabase database,
        params (int FiscalYear, long CurrentValue)[] periods)
    {
        var counter = await NewInvoiceRangeAsync(repository);
        await ApplyAsync(repository, Insert(counter));

        if (periods.Length > 0)
        {
            // The counters of the seeded fiscal years, in the shape the allocator writes.
            var entries = new BsonArray(periods.Select(p => PeriodDocument(p.FiscalYear, p.CurrentValue)));
            var collection = database.GetCollection<BsonDocument>("RtEntity_SystemAutoIncrement");
            var result = await collection.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(counter.RtId.ToString())),
                new BsonDocument("$set", new BsonDocument("attributes.periods", entries)),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, result.ModifiedCount);
        }
    }

    private static async Task<RtAutoIncrement> NewInvoiceRangeAsync(ITenantRepository repository)
    {
        var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
        counter.RtId = OctoObjectId.GenerateNewId();
        counter.RtWellKnownName = InvoiceRange;
        counter.Format = "RE-{yyyy}-{counter:D5}";
        counter.CurrentValue = 0;
        counter.End = 99999;
        counter.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
        counter.ReferenceTimeZone = Vienna;
        counter.Release = new RtNumberRangeReleaseRecord
        {
            StateAttribute = "DocumentState",
            ReleasedValues = new AttributeStringValueList(["Released", "Cancelled"])
        };
        return counter;
    }

    private static BsonDocument PeriodDocument(int fiscalYear, long currentValue)
    {
        var period = new RtAutoIncrementPeriodRecord();
        period.SetAttributeValue(nameof(RtAutoIncrementPeriodRecord.FiscalYear), AttributeValueTypesDto.Int, fiscalYear);
        period.SetAttributeValue(nameof(RtAutoIncrementPeriodRecord.CurrentValue), AttributeValueTypesDto.Int,
            currentValue);
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        BsonSerializer.LookupSerializer<RtRecord>().Serialize(BsonSerializationContext.CreateRoot(writer), period);
        return document;
    }

    /// <summary>Stores an invoice straight into its collection, bypassing the allocator and the lock.</summary>
    private static async Task<OctoObjectId> SeedInvoiceAsync(IMongoDatabase database, string? number, int state)
    {
        var id = ObjectId.GenerateNewId();
        var attributes = new BsonDocument { ["title"] = "seeded", ["documentState"] = state };
        if (number != null)
        {
            attributes["number"] = number;
        }

        var now = DateTime.UtcNow;
        await database.GetCollection<BsonDocument>(InvoiceCollection).InsertOneAsync(new BsonDocument
        {
            ["_id"] = id, ["ckTypeId"] = InvoiceTypeId.SemanticVersionedFullName, ["rtCreationDateTime"] = now,
            ["rtChangedDateTime"] = now, ["attributes"] = attributes
        }, cancellationToken: TestContext.Current.CancellationToken);
        return new OctoObjectId(id.ToString());
    }

    private static IEntityUpdateInfo<RtEntity> Insert(RtEntity entity) => EntityUpdateInfo<RtEntity>.CreateInsert(entity);

    private static async Task ApplyAsync(ITenantRepository repository, params IEntityUpdateInfo<RtEntity>[] updates)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session, updates, operationResult);
        Assert.False(operationResult.HasErrors, operationResult.GetMessages());
        await session.CommitTransactionAsync();
    }

    private static RtEntity Invoice(int state) => new(InvoiceTypeId, OctoObjectId.GenerateNewId(),
        new Dictionary<string, object?> { ["Title"] = "invoice", ["DocumentState"] = state });

    private async Task WithTenantAsync(string prefix, Func<ITenantRepository, IMongoDatabase, Task> body)
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

            await tenant.ImportCkModelAsync(Model(SystemCkIds.CkModelId));
            await body(tenant.GetTenantRepository(), GetTenantDatabase(tenantId));
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

    /// <summary>
    ///     Invoice (enum DocumentState, String Number from "GapInvoiceNumber", index on Number - the document model's
    ///     part, D12) and Note (Int Number from "GapNoteNumber", index on Number).
    /// </summary>
    private static CkCompiledModelRoot Model(CkModelId system)
    {
        var id = new CkModelId("GapReportTest-1.0.0");

        CkTypeIndexDto NumberIndex() => new()
        {
            IndexType = IndexTypeDto.Ascending, Fields = [new CkIndexFieldsDto { AttributePaths = ["Number"] }]
        };

        CkTypeAttributeDto Attribute(string attributeId, string name, string? reference = null) => new()
        {
            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId(attributeId)), AttributeName = name,
            IsOptional = true, AutoIncrementReference = reference
        };

        CkCompiledTypeDto Type(string typeName, params CkTypeAttributeDto[] attributes) => new()
        {
            TypeId = new CkTypeId($"{typeName}-1"), IsCollectionRoot = true,
            DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1")),
            Attributes = [Attribute("Title-1", "Title"), .. attributes],
            Indexes = [NumberIndex()]
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "Gap report of number ranges",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("DocumentState-1"), ValueType = AttributeValueTypesDto.Enum,
                    ValueCkEnumId = new CkId<CkEnumId>(id, new CkEnumId("DocumentStates-1"))
                },
                new CkAttributeDto
                    { AttributeId = new CkAttributeId("InvoiceNumber-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                    { AttributeId = new CkAttributeId("NoteNumber-1"), ValueType = AttributeValueTypesDto.Int }
            ],
            Enums =
            [
                new CkEnumDto
                {
                    EnumId = new CkEnumId("DocumentStates-1"),
                    Values =
                    [
                        new CkEnumValueDto { Key = 0, Name = "Draft" },
                        new CkEnumValueDto { Key = 1, Name = "Released" },
                        new CkEnumValueDto { Key = 2, Name = "Cancelled" }
                    ]
                }
            ],
            Types =
            [
                Type("Invoice", Attribute("DocumentState-1", "DocumentState"),
                    Attribute("InvoiceNumber-1", "Number", InvoiceRange)),
                Type("Note", Attribute("NoteNumber-1", "Number", NoteRange))
            ]
        };
    }
}
