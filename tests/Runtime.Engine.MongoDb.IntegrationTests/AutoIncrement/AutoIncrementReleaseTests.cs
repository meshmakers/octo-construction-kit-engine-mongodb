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
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     AB#9758 against a real MongoDB, in throwaway child tenants: a number range with a release configuration
///     (<c>AutoIncrement.Release</c>, <c>StateAttribute = DocumentState</c>, <c>ReleasedValues = [Released, Cancelled]</c>)
///     numbers a document only when it reaches a released state, in the write that releases it; a range without release
///     configuration numbers on insert exactly as before. Model: Invoice (<c>Number</c> from "InvoiceNumber") and
///     LegacyDoc (<c>Number</c> from "LegacyNumber"), both String, both with the enum <c>DocumentState</c>
///     (Draft = 0, Released = 1, Cancelled = 2).
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class AutoIncrementReleaseTests(CkModelImportGuardFixture fixture)
{
    private const string NumberAttribute = "Number";
    private const string StateAttribute = "DocumentState";
    private const string InvoiceFormat = "RE-{yyyy}-{counter:D5}";
    private const string Vienna = "Europe/Vienna";
    private const int Draft = 0;
    private const int Released = 1;

    private static readonly RtCkId<CkTypeId> InvoiceTypeId = new("ReleaseTest/Invoice");
    private static readonly RtCkId<CkTypeId> LegacyDocTypeId = new("ReleaseTest/LegacyDoc");

    /// <summary>AC 1: a draft gets no number, and the counter is not touched (no period, no version bump).</summary>
    [Fact]
    public async Task Draft_IsInsertedWithoutANumber_AndTheCounterIsUnchanged()
    {
        await WithTenantAsync("reldraft", async (repository, database, _) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var before = await CounterDocumentAsync(database, counterId);

            var draft = Invoice(Draft);
            await ApplyAsync(repository, Insert(draft));

            Assert.False((await AttributesAsync(database, draft.RtId)).Contains("number"));
            Assert.Equal(before, await CounterDocumentAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 2: the partial update that sets the state to Released numbers the draft in that same write: one
    ///     <c>ApplyChanges</c>, the stored document has the state and the number, the counter of the current fiscal
    ///     year is 1.
    /// </summary>
    [Fact]
    public async Task UpdateIntoTheReleaseState_NumbersTheDraft_InTheSameWrite()
    {
        await WithTenantAsync("relupdate", async (repository, database, _) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var draft = Invoice(Draft);
            await ApplyAsync(repository, Insert(draft));
            var yearBefore = ViennaYearNow();

            var release = StateUpdate(draft.RtId, Released);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release));

            var yearAfter = ViennaYearNow();
            var stored = await AttributesAsync(database, draft.RtId);
            Assert.Equal(Released, stored["documentState"].ToInt32());
            Assert.Contains(stored["number"].AsString, new[] { $"RE-{yearBefore}-00001", $"RE-{yearAfter}-00001" });
            Assert.Equal("invoice", stored["title"].AsString);
            Assert.Equal(1, (await PeriodsAsync(database, counterId)).Values.Single());
        });
    }

    /// <summary>AC 2, replace: a replace that releases a stored draft numbers it in the same write.</summary>
    [Fact]
    public async Task ReplaceIntoTheReleaseState_NumbersTheDraft()
    {
        await WithTenantAsync("relreplace", async (repository, database, _) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var draft = Invoice(Draft);
            await ApplyAsync(repository, Insert(draft));

            var replacement = Invoice(Released, draft.RtId);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateReplace(replacement.ToRtEntityId(),
                replacement));

            Assert.EndsWith("-00001", (await AttributesAsync(database, draft.RtId))["number"].AsString);
            Assert.Equal(1, (await PeriodsAsync(database, counterId)).Values.Single());
        });
    }

    /// <summary>
    ///     AC 2, D2: the release date decides the fiscal year, not the creation date. The draft is created now (2026 or
    ///     later); released at 00:30 on 1 January 2027 Vienna time (still 2026 in UTC) it gets <c>RE-2027-00001</c>, a
    ///     second draft released at 23:30 on 31 December 2026 gets <c>RE-2026-00001</c>.
    /// </summary>
    [Fact]
    public async Task TheReleaseDate_DecidesTheFiscalYear()
    {
        await WithTenantAsync("relyear", async (repository, database, dataSource) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var newYearDraft = Invoice(Draft);
            var oldYearDraft = Invoice(Draft);
            await ApplyAsync(repository, Insert(newYearDraft), Insert(oldYearDraft));
            var clock = new FakeClock(DateTimeOffset.Parse("2027-01-01T00:30:00+01:00"));
            var modifier = NewModifier(clock);

            var newYearRelease = StateUpdate(newYearDraft.RtId, Released);
            await ModifyUpdatesAsync(modifier, dataSource, newYearRelease);
            clock.Now = DateTimeOffset.Parse("2026-12-31T23:30:00+01:00");
            var oldYearRelease = StateUpdate(oldYearDraft.RtId, Released);
            await ModifyUpdatesAsync(modifier, dataSource, oldYearRelease);

            Assert.Equal("RE-2027-00001", Number(newYearRelease));
            Assert.Equal("RE-2026-00001", Number(oldYearRelease));
            Assert.Equal(new Dictionary<int, long> { { 2026, 1 }, { 2027, 1 } },
                await PeriodsAsync(database, counterId));
        });
    }

    /// <summary>AC 3: a document inserted directly in a released state is numbered on insert; a draft next to it is not.</summary>
    [Fact]
    public async Task InsertDirectlyInTheReleaseState_IsNumberedOnInsert()
    {
        await WithTenantAsync("relinsert", async (repository, database, _) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var draft = Invoice(Draft);
            var released = Invoice(Released);

            await ApplyAsync(repository, Insert(draft), Insert(released));

            Assert.EndsWith("-00001", (await AttributesAsync(database, released.RtId))["number"].AsString);
            Assert.False((await AttributesAsync(database, draft.RtId)).Contains("number"));
            Assert.Equal(1, (await PeriodsAsync(database, counterId)).Values.Single());
        });
    }

    /// <summary>AC 4: three drafts, one deleted, the other two released (in two writes): consecutive numbers, no gap.</summary>
    [Fact]
    public async Task ThreeDrafts_OneDeleted_TheOthersReleased_AreConsecutive()
    {
        await WithTenantAsync("relgap", async (repository, database, _) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var drafts = new[] { Invoice(Draft), Invoice(Draft), Invoice(Draft) };
            await ApplyAsync(repository, drafts.Select(Insert).ToArray());

            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateDelete(drafts[1].ToRtEntityId()));
            var first = StateUpdate(drafts[2].RtId, Released);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(first.ToRtEntityId(), first));
            var second = StateUpdate(drafts[0].RtId, Released);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(second.ToRtEntityId(), second));

            var firstNumber = (await AttributesAsync(database, drafts[2].RtId))["number"].AsString;
            var secondNumber = (await AttributesAsync(database, drafts[0].RtId))["number"].AsString;
            Assert.EndsWith("-00001", firstNumber);
            Assert.EndsWith("-00002", secondNumber);
            Assert.Equal(2, (await PeriodsAsync(database, counterId)).Values.Single());
        });
    }

    /// <summary>
    ///     AC 5: a released document updated again - an update of an attribute the range keeps changeable, an update that
    ///     sets the released state again, and a replace without the number - keeps its number, and the counter is not
    ///     advanced. (The lock of released documents refuses every other change.)
    /// </summary>
    [Fact]
    public async Task ReleasedDocument_UpdatedAgain_KeepsItsNumber_AndTheCounter()
    {
        await WithTenantAsync("relagain", async (repository, database, _) =>
        {
            var counter = await NewReleaseCounterAsync(repository, ["Released", "Cancelled"], changeable: ["Title"]);
            await ApplyAsync(repository, Insert(counter));
            var counterId = counter.RtId;
            var released = Invoice(Released);
            await ApplyAsync(repository, Insert(released));
            var number = (await AttributesAsync(database, released.RtId))["number"].AsString;
            var counterBefore = await CounterDocumentAsync(database, counterId);

            var retitled = new RtEntity(InvoiceTypeId, released.RtId,
                new Dictionary<string, object?> { ["Title"] = "retitled" });
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(retitled.ToRtEntityId(), retitled));
            var releasedAgain = StateUpdate(released.RtId, Released);
            await ApplyAsync(repository,
                EntityUpdateInfo<RtEntity>.CreateUpdate(releasedAgain.ToRtEntityId(), releasedAgain));
            var replacement = Invoice(Released, released.RtId);
            replacement.SetAttributeRawValue("Title", "retitled");
            await ApplyAsync(repository,
                EntityUpdateInfo<RtEntity>.CreateReplace(replacement.ToRtEntityId(), replacement));

            Assert.Equal(number, (await AttributesAsync(database, released.RtId))["number"].AsString);
            Assert.Equal(counterBefore, await CounterDocumentAsync(database, counterId));
        });
    }

    /// <summary>An update of a draft that does not set a released state (another attribute, or Draft again) takes no number.</summary>
    [Fact]
    public async Task DraftUpdate_WithoutARelease_TakesNoNumber()
    {
        await WithTenantAsync("reledit", async (repository, database, _) =>
        {
            var counterId = await InsertReleaseCounterAsync(repository);
            var draft = Invoice(Draft);
            await ApplyAsync(repository, Insert(draft));
            var counterBefore = await CounterDocumentAsync(database, counterId);

            var retitled = new RtEntity(InvoiceTypeId, draft.RtId,
                new Dictionary<string, object?> { ["Title"] = "retitled" });
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(retitled.ToRtEntityId(), retitled));
            var stillDraft = StateUpdate(draft.RtId, Draft);
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(stillDraft.ToRtEntityId(), stillDraft));

            Assert.False((await AttributesAsync(database, draft.RtId)).Contains("number"));
            Assert.Equal(counterBefore, await CounterDocumentAsync(database, counterId));
        });
    }

    /// <summary>
    ///     AC 6 (regression, energy-community counters): a range without release configuration numbers on insert exactly
    ///     as before - the draft gets <c>0042</c> on insert, releasing it later neither renumbers it nor advances the
    ///     counter, and the next document gets <c>0043</c>; no period and no release configuration appear.
    /// </summary>
    [Fact]
    public async Task RangeWithoutReleaseConfiguration_NumbersOnInsertAsBefore()
    {
        await WithTenantAsync("rellegacy", async (repository, database, _) =>
        {
            var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
            counter.RtId = OctoObjectId.GenerateNewId();
            counter.RtWellKnownName = "LegacyNumber";
            counter.Format = "{0:D4}";
            counter.CurrentValue = 41;
            counter.End = 9999;
            await ApplyAsync(repository, Insert(counter));

            var draft = LegacyDoc(Draft);
            await ApplyAsync(repository, Insert(draft));
            Assert.Equal("0042", Number(draft));

            var release = new RtEntity(LegacyDocTypeId, draft.RtId,
                new Dictionary<string, object?> { [StateAttribute] = Released });
            await ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateUpdate(release.ToRtEntityId(), release));
            var next = LegacyDoc(Released);
            await ApplyAsync(repository, Insert(next));

            Assert.Equal("0042", (await AttributesAsync(database, draft.RtId))["number"].AsString);
            Assert.Equal("0043", Number(next));
            var stored = (await CounterDocumentAsync(database, counter.RtId))["attributes"].AsBsonDocument;
            Assert.Equal(43, stored["currentValue"].ToInt64());
            Assert.DoesNotContain(stored.Names, n => n is "periods" or "release");
        });
    }

    /// <summary>A release configuration without released values is refused when the range is configured; nothing is stored.</summary>
    [Fact]
    public async Task ConfiguringAReleaseWithoutReleasedValues_IsRefused()
    {
        await WithTenantAsync("relrules", async (repository, database, _) =>
        {
            var counter = await NewReleaseCounterAsync(repository, []);

            var exception = await Record.ExceptionAsync(() => ApplyAsync(repository, Insert(counter)));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("ReleasedValues", refused.Message);
            Assert.Null(await TryFindDocumentAsync(database, counter.RtId));
        });
    }

    /// <summary>A release configuration whose state attribute the document type does not have numbers nothing: refused.</summary>
    [Fact]
    public async Task ReleaseWithAnUnknownStateAttribute_IsRefusedWhenADocumentIsWritten()
    {
        await WithTenantAsync("relstate", async (repository, database, _) =>
        {
            var counter = await NewReleaseCounterAsync(repository, ["Released"], stateAttribute: "Status");
            await ApplyAsync(repository, Insert(counter));

            var invoice = Invoice(Released);
            var exception = await Record.ExceptionAsync(() => ApplyAsync(repository, Insert(invoice)));

            var refused = Assert.IsType<AutoIncrementFailedException>(exception);
            Assert.Contains("Status", refused.Message);
            Assert.Null(await TryFindDocumentAsync(database, invoice.RtId));
        });
    }

    private async Task<OctoObjectId> InsertReleaseCounterAsync(ITenantRepository repository)
    {
        var counter = await NewReleaseCounterAsync(repository, ["Released", "Cancelled"]);
        await ApplyAsync(repository, Insert(counter));
        return counter.RtId;
    }

    private static async Task<RtAutoIncrement> NewReleaseCounterAsync(ITenantRepository repository,
        List<string> releasedValues, string stateAttribute = StateAttribute, List<string>? changeable = null)
    {
        var counter = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
        counter.RtId = OctoObjectId.GenerateNewId();
        counter.RtWellKnownName = "InvoiceNumber";
        counter.Format = InvoiceFormat;
        counter.CurrentValue = 0;
        counter.End = 99999;
        counter.PeriodKind = RtAutoIncrementPeriodKindsEnum.FiscalYear;
        counter.ReferenceTimeZone = Vienna;
        counter.Release = new RtNumberRangeReleaseRecord
        {
            StateAttribute = stateAttribute,
            ReleasedValues = new AttributeStringValueList(releasedValues),
            ChangeableAttributes = changeable == null ? null : new AttributeStringValueList(changeable)
        };
        return counter;
    }

    private AutoIncrementModifier NewModifier(TimeProvider clock) =>
        new(fixture.GetService<ICkCacheService>(), NullLogger<AutoIncrementModifier>.Instance, clock);

    private static async Task ModifyUpdatesAsync(AutoIncrementModifier modifier,
        MongoDbRepositoryDataSource dataSource, params RtEntity[] partials)
    {
        using var session = await dataSource.GetSessionAsync();
        await modifier.ModifyUpdatesAsync(session, dataSource, partials);
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

    private static RtEntity Invoice(int state, OctoObjectId? rtId = null) => new(InvoiceTypeId,
        rtId ?? OctoObjectId.GenerateNewId(),
        new Dictionary<string, object?> { ["Title"] = "invoice", [StateAttribute] = state });

    private static RtEntity LegacyDoc(int state) => new(LegacyDocTypeId, OctoObjectId.GenerateNewId(),
        new Dictionary<string, object?> { ["Title"] = "legacy", [StateAttribute] = state });

    private static RtEntity StateUpdate(OctoObjectId rtId, int state) => new(InvoiceTypeId, rtId,
        new Dictionary<string, object?> { [StateAttribute] = state });

    private static string Number(RtEntity entity) =>
        Assert.IsType<string>(entity.GetAttributeValueOrDefault(NumberAttribute));

    private static int ViennaYearNow() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Vienna)).Year;

    private static async Task<BsonDocument> AttributesAsync(IMongoDatabase database, OctoObjectId rtId) =>
        (await TryFindDocumentAsync(database, rtId) ??
         throw new InvalidOperationException($"Entity {rtId} not found"))["attributes"].AsBsonDocument;

    private static async Task<BsonDocument> CounterDocumentAsync(IMongoDatabase database, OctoObjectId counterId) =>
        await TryFindDocumentAsync(database, counterId) ??
        throw new InvalidOperationException($"Counter {counterId} not found");

    private static async Task<Dictionary<int, long>> PeriodsAsync(IMongoDatabase database, OctoObjectId counterId)
    {
        var counter = (await CounterDocumentAsync(database, counterId))["attributes"].AsBsonDocument;
        return counter["periods"].AsBsonArray.Select(p => p["attributes"].AsBsonDocument)
            .ToDictionary(p => p["fiscalYear"].ToInt32(), p => p["currentValue"].ToInt64());
    }

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

    /// <summary>A throwaway tenant with the Invoice/LegacyDoc model; counters are added by each test.</summary>
    private async Task WithTenantAsync(string prefix,
        Func<ITenantRepository, IMongoDatabase, MongoDbRepositoryDataSource, Task> body)
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

            var database = GetTenantDatabase(tenantId);
            var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
                fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(database.DatabaseNamespace.DatabaseName),
                database.DatabaseNamespace.DatabaseName, database.DatabaseNamespace.DatabaseName);
            await body(tenant.GetTenantRepository(), database, dataSource);
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

    /// <summary>Invoice and LegacyDoc, each with Title, the enum DocumentState and a String Number from its own counter.</summary>
    internal static CkCompiledModelRoot DocumentModel(CkModelId system)
    {
        var id = new CkModelId("ReleaseTest-1.0.0");
        var stateEnumId = new CkId<CkEnumId>(id, new CkEnumId("DocumentStates-1"));

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
                    CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("DocumentState-1")),
                    AttributeName = StateAttribute, IsOptional = true
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
            Description = "AB#9758 numbering on release",
            Dependencies = [system],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Title-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("DocumentState-1"), ValueType = AttributeValueTypesDto.Enum,
                    ValueCkEnumId = stateEnumId
                },
                new CkAttributeDto
                    { AttributeId = new CkAttributeId("DocumentNumber-1"), ValueType = AttributeValueTypesDto.String }
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
                DocumentType("Invoice", "InvoiceNumber"),
                DocumentType("LegacyDoc", "LegacyNumber")
            ]
        };
    }

    /// <summary>A clock under the test's control.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset Now
        {
            set => _now = value;
        }

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    }
}
