using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>AB#7991: the blueprint service on tenants with stream data on a real CrateDB, of its own.</summary>
[CollectionDefinition(Name)]
public class BlueprintArchiveActivationCollection : ICollectionFixture<BlueprintArchiveActivationFixture>
{
    public const string Name = "BlueprintArchiveActivation";
}
