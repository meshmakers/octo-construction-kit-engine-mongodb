using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     <see cref="StreamDataFlagFixture" /> that additionally enables stream data on the system tenant
///     (which imports System.StreamData 1.8.0) and then ImportRt-seeds the rollup fixtures under
///     <c>testData/rollupSources</c>: two shipped seeds in the deprecated single-scalar form
///     (EnergyCommunity.Base blueprint, energy simulator sample) and one new-form fragment declaring
///     <c>RollupArchive.Sources</c> only. AB#5157 seed compatibility.
/// </summary>
/// <remarks>
///     The import runs once for the whole collection and its outcome is exposed through
///     <see cref="ImportException" /> instead of failing the fixture, so the "a 1.7.x seed still
///     imports" assertion lives in a test with a readable failure message rather than in fixture
///     initialisation.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public class RollupArchiveSourceSeedFixture : StreamDataFlagFixture
{
    /// <summary>The EnergyCommunity.Base blueprint seed's RollupArchive entities (old form, versioned CK ids).</summary>
    public const string EnergyCommunitySeedPath = "testData/rollupSources/legacyEnergyCommunitySeed.yaml";

    /// <summary>The energy simulator sample's chained rollups (old form, unversioned CK ids).</summary>
    public const string SimulatorSeedPath = "testData/rollupSources/legacySimulatorSeed.yaml";

    /// <summary>A rollup declaring two time-disjoint sources through <c>Sources</c> only (new form).</summary>
    public const string MultiSourceSeedPath = "testData/rollupSources/multiSourceSeed.yaml";

    /// <summary>The exception the seed import failed with, or <c>null</c> when every seed imported.</summary>
    public Exception? ImportException { get; private set; }

    /// <summary>The system tenant the seeds were imported into.</summary>
    public ITenantContext TenantContext { get; private set; } = null!;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        TenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await TenantContext.EnableStreamDataAsync();

        var repository = systemContext.GetSystemTenantRepository();
        try
        {
            foreach (var seedPath in new[] { EnergyCommunitySeedPath, SimulatorSeedPath, MultiSourceSeedPath })
            {
                // Transient command: each file gets its own instance, the import state is per-command.
                await GetService<IImportRtModelCommand>().ImportAsync(repository, seedPath,
                    ExchangeMimeTypes.MimeTypeYaml, ImportStrategy.Insert);
            }
        }
        catch (Exception e)
        {
            ImportException = e;
        }
    }
}
