using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     Stream data enabled at instance level, the System.StreamData descriptor and a FAKE stream-data repository
///     factory: the archive lifecycle service exists (it needs a repository) but nothing talks to CrateDB.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ArchiveHiddenAccessFixture : SystemFixture
{
    public ArchiveHiddenAccessFixture()
    {
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));
        Services.AddSingleton(A.Fake<IStreamDataRepositoryFactory>());
    }
}

[CollectionDefinition(Name)]
public class ArchiveHiddenAccessCollection : ICollectionFixture<ArchiveHiddenAccessFixture>
{
    public const string Name = "ArchiveHiddenAccess";
}

/// <summary>
///     CK v2 review G3 E-M2, the engine-mongodb wiring: no archive column may reach a Hidden attribute.
///     <list type="bullet">
///         <item>
///             An ACTIVE archive whose column reaches an attribute that a later model import makes Hidden goes
///             <c>Failed</c> (stops ingesting); an archive on an unaffected column stays <c>Activated</c>
///             (<c>TenantContext.RevalidateArchiveAccessAsync</c> after the import).
///         </item>
///         <item>
///             The CrateDB column builder refuses a Hidden column and a whole-record column whose record contains a
///             Hidden sub-attribute (<c>ArchiveHiddenColumnGuard</c>), and accepts the record's visible sub-path.
///         </item>
///     </list>
/// </summary>
[Collection(ArchiveHiddenAccessCollection.Name)]
public class ArchiveHiddenAccessTests(ArchiveHiddenAccessFixture fixture)
{
    [Fact]
    public async Task ActiveArchive_GoesFailed_WhenAModelImportMakesItsColumnHidden()
    {
        await WithTenantAsync("archhidden", async tenant =>
        {
            var systemId = await InstalledSystemAsync(tenant);
            await tenant.EnableStreamDataAsync();
            await tenant.ImportCkModelAsync(MeterModel(new CkModelId("ArchAcc-1.0.0"), systemId, pinHidden: false));

            var pinArchive = await InsertActivatedArchiveAsync(tenant, "PinArchive", "Pin");
            var nameArchive = await InsertActivatedArchiveAsync(tenant, "NameArchive", "Name");

            // The next version makes Pin Hidden.
            await tenant.ImportCkModelAsync(MeterModel(new CkModelId("ArchAcc-1.1.0"), systemId, pinHidden: true));

            var store = tenant.GetArchiveRuntimeStore();
            Assert.Equal(CkArchiveStatus.Failed, (await store.GetAsync(pinArchive))!.Status);
            Assert.Equal(CkArchiveStatus.Activated, (await store.GetAsync(nameArchive))!.Status);
        });
    }

    [Fact]
    public async Task ColumnBuilder_RefusesHiddenAndWholeRecordWithHiddenSubAttribute()
    {
        await WithTenantAsync("archcols", async tenant =>
        {
            var systemId = await InstalledSystemAsync(tenant);
            await tenant.ImportCkModelAsync(MeterModel(new CkModelId("ArchCol-1.0.0"), systemId, pinHidden: true));
            await tenant.LoadCacheForTenantAsync();
            var cache = fixture.GetService<ICkCacheService>();
            var meter = new RtCkId<CkTypeId>("ArchCol/Meter");

            IReadOnlyList<ArchiveColumnDdl> Resolve(string path) => ArchivePathTypeResolver.Resolve(cache,
                tenant.TenantId, meter, [new CkArchiveColumnSpec(path, Indexed: false, Required: false)]);

            Assert.Single(Resolve("Name"));
            Assert.Single(Resolve("Reading.Value"));
            Assert.Contains("Hidden", Assert.Throws<UnresolvableArchivePathException>(() => Resolve("Pin")).Message);
            Assert.Contains("Reading.Token",
                Assert.Throws<UnresolvableArchivePathException>(() => Resolve("Reading")).Message);
            // Case-insensitive like the database.
            Assert.Throws<UnresolvableArchivePathException>(() => Resolve("pin"));
        });
    }

    /// <summary>
    ///     Meter (collection root): Name, Pin (Hidden when <paramref name="pinHidden" />), Reading (record with a
    ///     visible Value and a Hidden Token).
    /// </summary>
    private static CkCompiledModelRoot MeterModel(CkModelId id, CkModelId systemId, bool pinHidden)
    {
        CkTypeAttributeDto Assign(string name, CkAttributeAccessDto? access = null) => new()
        {
            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId($"{name}-1")), AttributeName = name,
            IsOptional = true, Access = access
        };

        return new CkCompiledModelRoot
        {
            ModelId = id,
            CkLanguage = 2,
            MinEngineVersion = "3.4.0",
            Dependencies = [systemId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Name-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Pin-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("Value-1"), ValueType = AttributeValueTypesDto.Double },
                new CkAttributeDto { AttributeId = new CkAttributeId("Token-1"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("Reading-1"), ValueType = AttributeValueTypesDto.Record,
                    ValueCkRecordId = new CkId<CkRecordId>(id, new CkRecordId("Reading-1"))
                }
            ],
            Records =
            [
                new CkRecordDto
                {
                    RecordId = new CkRecordId("Reading-1"),
                    Attributes = [Assign("Value"), Assign("Token", CkAttributeAccessDto.Hidden)]
                }
            ],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Meter-1"), IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                    Attributes =
                    [
                        Assign("Name"), Assign("Pin", pinHidden ? CkAttributeAccessDto.Hidden : null), Assign("Reading")
                    ]
                }
            ]
        };
    }

    private static async Task<OctoObjectId> InsertActivatedArchiveAsync(ITenantContext tenant, string name, string column)
    {
        var archive = new RtRawArchive
        {
            RtWellKnownName = name,
            TargetCkTypeId = "ArchAcc/Meter",
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = column, Indexed = false, Required = false }
            }
        };

        var repository = tenant.GetTenantRepository();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        await tenant.GetArchiveRuntimeStore().SetStatusAsync(archive.RtId, CkArchiveStatus.Activated);
        return archive.RtId;
    }

    private async Task<CkModelId> InstalledSystemAsync(ITenantContext tenant)
    {
        var catalogService = fixture.GetService<ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await tenant.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found in the catalogs.");
    }

    private async Task WithTenantAsync(string prefix, Func<ITenantContext, Task> body)
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

            await body(tenant);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId, dropStreamData: false);
        }
    }
}
