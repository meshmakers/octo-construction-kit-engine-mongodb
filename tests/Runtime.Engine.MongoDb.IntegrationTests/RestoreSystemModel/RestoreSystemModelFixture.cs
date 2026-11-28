using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Engine.ModelCatalogs;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RestoreSystemModel;

/// <summary>
///     System tenant whose platform version of System.Identity is the published 3.8.0 (installed the way the identity
///     service's embedded import does), and a CK catalog of exactly two catalogs: the engine's embedded models (System and
///     the test models) and <see cref="TestSystemIdentityCatalog" />, which publishes System.Identity 3.8.0 (the real
///     published file) and 3.7.0 (the same content without the write restriction, standing in for a genuine published
///     version below the platform's). Tests may publish further models from their own compiled files
///     (<see cref="TestSystemIdentityCatalog.PublishFile" />, e.g. the published System.UI of AB#10061) and withdraw them
///     again. No network catalog takes part, so every catalog answer is known.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class RestoreSystemModelFixture : SystemFixture
{
    public const string IdentityName = "System.Identity";
    public static readonly CkModelId Identity380 = new(IdentityName, "3.8.0");
    public static readonly CkModelId Identity370 = new(IdentityName, "3.7.0");

    public RestoreSystemModelFixture()
    {
        Services.RemoveAll<ICatalog>();
        Services.AddTransient<ICatalog, EmbeddedResourceCatalog>();
        Services.AddSingleton(Catalog);
        Services.AddSingleton<ICatalog>(sp => sp.GetRequiredService<TestSystemIdentityCatalog>());
        Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);

        // AB#10017 failure injection: a CK migration that throws or reports a failure, and an attach that fails.
        Decorate<ICkModelUpgradeService>((_, inner) => new FaultInjectingCkModelUpgradeService(inner, Faults));
        Decorate<ITenantNotifications>((_, inner) => new FaultInjectingTenantNotifications(inner, Faults));
    }

    public TestSystemIdentityCatalog Catalog { get; } = new();

    /// <summary>Failures the restore tests inject (off by default; a test sets and resets them).</summary>
    public RestoreFaults Faults { get; } = new();

    /// <summary>Every log line of the fixture's services (the scratch database name of a restore is in one).</summary>
    public RecordingLoggerProvider Logs { get; } = new();

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();
        var serializer = GetService<ICkJsonSerializer>();
        var path = Path.Combine(AppContext.BaseDirectory, "testData", "ck-system.identity-3.8.0.json");
        Catalog.Initialize(serializer, await File.ReadAllTextAsync(path));
        Catalog.Publish(Identity380);
        Catalog.Publish(Identity370, WithoutWriteAccess);

        await CkModelImportGuardTests.InstallAsServiceAsync(GetSystemContext(), await Catalog.LoadAsync(Identity380));
    }

    /// <summary>The 3.8.0 content without any write restriction.</summary>
    public static void WithoutWriteAccess(CkCompiledModelRoot model)
    {
        foreach (var type in model.Types ?? [])
        {
            type.WriteAccess = null;
        }
    }

    /// <summary>Replaces the last registration of <typeparamref name="TService" /> by a wrapper around it.</summary>
    private void Decorate<TService>(Func<IServiceProvider, TService, TService> wrap) where TService : class
    {
        var descriptor = Services.Last(d => d.ServiceType == typeof(TService));
        Services.Remove(descriptor);
        Services.Add(ServiceDescriptor.Describe(typeof(TService),
            sp => wrap(sp, (TService)(descriptor.ImplementationInstance
                                      ?? descriptor.ImplementationFactory?.Invoke(sp)
                                      ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!))),
            descriptor.Lifetime));
    }
}

/// <summary>What the restore tests make fail. Reset by the test that set it (the collection runs one test at a time).</summary>
public sealed class RestoreFaults
{
    /// <summary>How every CK migration run behaves.</summary>
    public MigrationFault Migration { get; set; }

    /// <summary>The attach of this tenant id fails (its pre-create notification throws).</summary>
    public string? FailAttachOfTenant { get; set; }

    /// <summary>Number of migration runs that met an armed <see cref="Migration" /> fault.</summary>
    public int MigrationFaultsHit;
}

public enum MigrationFault
{
    None,
    Throw,
    ReportFailure
}

/// <summary>The real upgrade service, unless <see cref="RestoreFaults.Migration" /> makes every run fail.</summary>
internal sealed class FaultInjectingCkModelUpgradeService(ICkModelUpgradeService inner, RestoreFaults faults)
    : ICkModelUpgradeService
{
    public Task<CkModelUpgradeResult> UpgradeModelsAsync(string tenantId, IEnumerable<CkModelIdVersionRange> newModelIds,
        CkMigrationOptions? options = null, IReadOnlyDictionary<string, string>? previouslyInstalledVersions = null,
        CancellationToken cancellationToken = default)
    {
        switch (faults.Migration)
        {
            case MigrationFault.Throw:
                Interlocked.Increment(ref faults.MigrationFaultsHit);
                throw new InvalidOperationException("Injected CK migration failure (test).");
            case MigrationFault.ReportFailure:
                Interlocked.Increment(ref faults.MigrationFaultsHit);
                return Task.FromResult(new CkModelUpgradeResult
                {
                    Success = false, Errors = ["Injected CK migration error (test)."]
                });
            default:
                return inner.UpgradeModelsAsync(tenantId, newModelIds, options, previouslyInstalledVersions,
                    cancellationToken);
        }
    }

    public Task<CkModelUpgradeInfo> CheckUpgradeNeededAsync(string tenantId, string ckModelName, string targetVersion,
        CancellationToken cancellationToken = default) =>
        inner.CheckUpgradeNeededAsync(tenantId, ckModelName, targetVersion, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetInstalledVersionsAsync(string tenantId,
        CancellationToken cancellationToken = default) =>
        inner.GetInstalledVersionsAsync(tenantId, cancellationToken);

    public Task RecordInstalledVersionAsync(string tenantId, CkModelId modelId,
        CancellationToken cancellationToken = default) =>
        inner.RecordInstalledVersionAsync(tenantId, modelId, cancellationToken);
}

/// <summary>The real notifications, except that the pre-create notification of an armed tenant id throws.</summary>
internal sealed class FaultInjectingTenantNotifications(ITenantNotifications inner, RestoreFaults faults)
    : ITenantNotifications
{
    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId) =>
        string.Equals(faults.FailAttachOfTenant, tenantId, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("Injected attach failure (test).")
            : inner.NotifyPreTenantCreateAsync(tenantId, correlationId);

    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId) =>
        inner.NotifyPosTenantCreateAsync(tenantId, correlationId);

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId) =>
        inner.NotifyPreTenantUpdateAsync(tenantId, correlationId);

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId) =>
        inner.NotifyPosTenantUpdateAsync(tenantId, correlationId);

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId) =>
        inner.NotifyPreTenantDeleteAsync(tenantId, correlationId);

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId) =>
        inner.NotifyPosTenantDeleteAsync(tenantId, correlationId);
}

[CollectionDefinition(Name)]
public class RestoreSystemModelCollection : ICollectionFixture<RestoreSystemModelFixture>
{
    public const string Name = "RestoreSystemModel";
}

/// <summary>
///     An in-memory catalog of System.Identity versions built from the published 3.8.0 file: each version is the file with
///     its id replaced and an optional change applied. Every read returns a fresh copy. <see cref="Unreachable" /> makes
///     every call fail like a catalog the host cannot reach.
/// </summary>
public sealed class TestSystemIdentityCatalog : ICatalog
{
    private readonly ConcurrentDictionary<CkModelId, Action<CkCompiledModelRoot>?> _published = new();
    private readonly ConcurrentDictionary<CkModelId, string> _files = new();
    private ICkJsonSerializer? _serializer;
    private string _publishedJson = string.Empty;

    /// <summary>When true, every read throws.</summary>
    public bool Unreachable { get; set; }

    public int Order => -10;

    public string CatalogName => "TestSystemIdentityCatalog";

    public string Description => "System.Identity versions of the restore check tests";

    public bool CanWrite => false;

    public bool CanRead => true;

    internal void Initialize(ICkJsonSerializer serializer, string publishedJson)
    {
        _serializer = serializer;
        _publishedJson = publishedJson;
    }

    public void Publish(CkModelId modelId, Action<CkCompiledModelRoot>? change = null) => _published[modelId] = change;

    /// <summary>Publishes <paramref name="modelId" /> with the compiled file <paramref name="json" /> (and a change).</summary>
    public void PublishFile(CkModelId modelId, string json, Action<CkCompiledModelRoot>? change = null)
    {
        _files[modelId] = json;
        _published[modelId] = change;
    }

    public void Withdraw(CkModelId modelId)
    {
        _published.TryRemove(modelId, out _);
        _files.TryRemove(modelId, out _);
    }

    /// <summary>
    ///     The published content of <paramref name="modelId" />: its own file (<see cref="PublishFile" />), else the 3.8.0
    ///     file renamed; with a change.
    /// </summary>
    public async Task<CkCompiledModelRoot> LoadAsync(CkModelId modelId, Action<CkCompiledModelRoot>? change = null)
    {
        var json = _files.TryGetValue(modelId, out var file)
            ? file
            : _publishedJson.Replace(RestoreSystemModelFixture.Identity380.FullName, modelId.FullName,
                StringComparison.Ordinal);
        var result = new OperationResult();
        var model = await _serializer!.DeserializeCompiledModelRootAsync(json, "test", result);
        Assert.False(result.HasErrors, string.Join("; ", result.Messages));
        change?.Invoke(model);
        return model;
    }

    public Task RefreshCatalogAsync(object? sourceIdentifier = null, bool forceRefresh = false) => Task.CompletedTask;

    public bool IsSupportingSourceIdentifier(object? sourceIdentifier = null) => true;

    public Task<ModelExistingResult> IsExistingAsync(CkModelIdVersionRange modelIdVersionRange,
        object? sourceIdentifier = null)
    {
        ThrowIfUnreachable();
        var best = _published.Keys
            .Where(id => string.Equals(id.Name, modelIdVersionRange.Name, StringComparison.OrdinalIgnoreCase) &&
                         modelIdVersionRange.ModelVersionRange.IsSatisfiedBy(id.Version))
            .OrderByDescending(id => id.Version)
            .FirstOrDefault();
        return Task.FromResult(new ModelExistingResult
        {
            Exists = best != null, ModelId = best, CatalogName = best != null ? CatalogName : null
        });
    }

    public Task<bool> IsExistingAsync(CkModelId modelId, object? sourceIdentifier = null)
    {
        ThrowIfUnreachable();
        return Task.FromResult(_published.ContainsKey(modelId));
    }

    public async Task<CkCompiledModelRoot> GetAsync(CkModelId modelId, OperationResult operationResult,
        object? sourceIdentifier = null, CancellationToken? cancellationToken = null)
    {
        ThrowIfUnreachable();
        if (!_published.TryGetValue(modelId, out var change))
        {
            throw new InvalidOperationException($"Model {modelId} is not in the test catalog.");
        }

        return await LoadAsync(modelId, change);
    }

    public Task PublishAsync(CkCompiledModelRoot ckCompiledModel, bool force = false, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null) => throw new NotSupportedException();

    public Task<CatalogUnpublishResult> UnpublishAsync(CkModelId modelId, string? reason = null,
        object? sourceIdentifier = null, CancellationToken? cancellationToken = null) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<CatalogResultItem> ListAsync(object? sourceIdentifier)
    {
        ThrowIfUnreachable();
        await Task.CompletedTask;
        foreach (var id in _published.Keys)
        {
            yield return new CatalogResultItem { ModelId = id, CatalogName = CatalogName };
        }
    }

    public async IAsyncEnumerable<CatalogResultItem> SearchAsync(string searchTerm, object? sourceIdentifier)
    {
        await foreach (var item in ListAsync(sourceIdentifier))
        {
            if (item.ModelId.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    ///     Makes the catalog answer exactly <paramref name="reads" /> more reads and become unreachable afterwards (a
    ///     catalog that goes away in the middle of a restore); <c>null</c> turns it off.
    /// </summary>
    public void UnreachableAfter(int? reads)
    {
        _readsLeft = reads ?? -1;
        _countReads = reads != null;
    }

    private int _readsLeft = -1;
    private volatile bool _countReads;

    private void ThrowIfUnreachable([CallerMemberName] string member = "")
    {
        if (Unreachable || (_countReads && Interlocked.Decrement(ref _readsLeft) < 0))
        {
            throw new HttpRequestException($"Test catalog unreachable ({member}).");
        }
    }
}
