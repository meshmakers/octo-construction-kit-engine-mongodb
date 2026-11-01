using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// <see cref="BlueprintServiceFixture" /> with stream data on: the system tenant holds System.StreamData, and a child
/// tenant gets it through <see cref="EnableStreamDataAsync" />. A FAKE stream data repository (no CrateDB) makes the archive
/// lifecycle exist, so an import that adds a column to an activated archive (AB#7997) reaches it; the tests read the
/// storage calls from <see cref="StreamDataRepository" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ArchiveDefinitionGuardFixture : BlueprintServiceFixture
{
    public ArchiveDefinitionGuardFixture()
    {
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));

        // The formula of a computed column comes back as written (the physical rewrite is the CrateDB repository's).
        A.CallTo(() => StreamDataRepository.NormalizeComputedFormulaAsync(A<OctoObjectId>._,
                A<IReadOnlyList<CkArchiveColumnSpec>>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((OctoObjectId _, IReadOnlyList<CkArchiveColumnSpec> _, string formula, CancellationToken _) => formula);
        var factory = A.Fake<IStreamDataRepositoryFactory>();
        A.CallTo(() => factory.Create(A<string>._, A<IArchiveRuntimeStore>._, A<IRollupArchiveRuntimeStore?>._,
            A<IArchiveRecomputeStateStore?>._)).Returns(StreamDataRepository);
        Services.AddSingleton(factory);
    }

    /// <summary>The fake stream data repository of every tenant of this fixture.</summary>
    public IStreamDataRepository StreamDataRepository { get; } = A.Fake<IStreamDataRepository>();

    /// <summary>The system tenant, with stream data enabled.</summary>
    public ITenantContext TenantContext { get; private set; } = null!;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        TenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await TenantContext.EnableStreamDataAsync();
    }

    /// <summary>Enables stream data on a child tenant.</summary>
    public async Task<ITenantContext> EnableStreamDataAsync(string tenantId)
    {
        var tenant = await GetSystemContext().FindTenantContextAsync(tenantId);
        await tenant.EnableStreamDataAsync();
        return tenant;
    }
}
