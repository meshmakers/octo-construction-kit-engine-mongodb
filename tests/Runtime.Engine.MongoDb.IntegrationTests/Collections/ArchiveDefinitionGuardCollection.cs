using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>AB#7872: a stream data enabled tenant with the blueprint service, of its own.</summary>
[CollectionDefinition(Name)]
public class ArchiveDefinitionGuardCollection : ICollectionFixture<ArchiveDefinitionGuardFixture>
{
    public const string Name = "ArchiveDefinitionGuard";
}
