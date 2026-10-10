using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Own <see cref="BlueprintServiceFixture" /> for the seed value guard tests (AB#6313), so they run
///     concurrently with <c>BlueprintServiceIntegrationTests</c> instead of lengthening its collection.
/// </summary>
[CollectionDefinition(Name)]
public class SeedValueGuardCollection : ICollectionFixture<BlueprintServiceFixture>
{
    public const string Name = "SeedValueGuard";
}
