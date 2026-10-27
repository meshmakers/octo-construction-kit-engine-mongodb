using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>Shares one <see cref="InterfaceQueryFixture" /> across the interface query tests (AB#7019).</summary>
[CollectionDefinition(Name)]
public class InterfaceQueryCollection : ICollectionFixture<InterfaceQueryFixture>
{
    public const string Name = "InterfaceQuery";
}
