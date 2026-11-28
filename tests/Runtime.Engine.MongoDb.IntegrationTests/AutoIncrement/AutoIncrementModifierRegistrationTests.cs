using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     A host that registers the runtime repository twice (a test fixture and a service extension that both call
///     <c>AddMongoDbRuntimeRepository</c>) gets every pre-document modification once: a second number modifier would take
///     the number the first one handed out for a number the caller supplied, which a range that numbers on release
///     refuses.
/// </summary>
public class AutoIncrementModifierRegistrationTests
{
    [Fact]
    public void ASecondRegistration_AddsNoSecondModifierOrValidator()
    {
        var services = new ServiceCollection();
        services.AddRuntimeEngine().AddMongoDbRuntimeRepository();
        services.AddRuntimeEngine().AddMongoDbRuntimeRepository();

        Assert.Equal(1, Count<IPreDocumentModification<RtEntity>, AutoIncrementModifier>(services));
        Assert.Equal(1, Count<IPreDocumentUpdateModification<RtEntity>, AutoIncrementModifier>(services));
        Assert.Equal(1, Count<IPreDocumentModification<RtEntity>, AutoIncrementRangeValidator>(services));
        Assert.Equal(1, Count<IPreDocumentUpdateValidation<RtEntity>, AutoIncrementRangeValidator>(services));
        Assert.Equal(1, Count<IPreDocumentModification<RtEntity>, DisplayNameModifier>(services));
    }

    private static int Count<TService, TImplementation>(IServiceCollection services) =>
        services.Count(d => d.ServiceType == typeof(TService) && d.ImplementationType == typeof(TImplementation));
}
