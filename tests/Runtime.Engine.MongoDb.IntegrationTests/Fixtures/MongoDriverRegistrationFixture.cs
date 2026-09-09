using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

[assembly: AssemblyFixture(typeof(MongoDriverRegistrationFixture))]

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Configures the MongoDB driver — BSON conventions and the hand-written class maps in
///     <c>MongoRepositoryClient.RegisterClassMaps</c> — exactly once, before any test collection runs.
///     <para>
///         Those class maps are process-global and <b>order-sensitive</b>:
///         <c>RtTypeWithAttributes</c> must get OUR map (its <c>_attributes</c> field bound to
///         <c>RtAttributeDictionarySerializer</c>) before anything asks the driver for a serializer of
///         an <c>RtEntity</c> / <c>RtRecord</c>, because that lookup makes the driver auto-generate a
///         map for the whole base chain. <c>RegisterClassMaps</c> deliberately refuses to run against
///         such a pre-populated registry (<c>TenantException.CannotRegisterBecauseAlreadyRegistered</c>)
///         rather than silently accepting an auto-generated map that would drop the attribute
///         serializer.
///     </para>
///     <para>
///         With <c>parallelizeTestCollections: true</c> (AB#5116) collection order is arbitrary, so a
///         pure serializer test that touches BSON without going through a repository client — e.g.
///         <c>AttributeArrayValuePolymorphicWrapperTests</c> (AB#5160) — can win that race and poison
///         the registry for the whole process. An assembly fixture is created and initialized before
///         any collection, which is the only place that can establish the ordering for every test at
///         once.
///     </para>
///     <para>
///         No database is involved: the repository client's <c>MongoClient</c> is created lazily on
///         first use, so constructing it only runs the registration. The provider is deliberately kept
///         alive (never disposed) — disposing it would dispose the repository client, and its
///         <c>Dispose</c> touches the lazy <c>Client</c> property and would open a real connection.
///     </para>
/// </summary>
public sealed class MongoDriverRegistrationFixture
{
    // Referenced only to keep the provider (and with it the registration) alive for the process.
    private readonly ServiceProvider _provider;

    public MongoDriverRegistrationFixture()
    {
        var services = new ServiceCollection();
        services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();
        services.AddCkModelTestV1();
        services.AddLogging(loggingBuilder => loggingBuilder.ClearProviders());

        _provider = services.BuildServiceProvider();

        // Resolving ISystemContext constructs the admin repository client, whose constructor runs
        // MongoRepositoryClient.ConfigureMongoDriver. Default OctoSystemConfiguration values are
        // enough — nothing connects.
        _ = _provider.GetRequiredService<ISystemContext>();
    }
}
