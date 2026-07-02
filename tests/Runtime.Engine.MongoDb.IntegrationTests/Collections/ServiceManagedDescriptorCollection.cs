using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="ServiceManagedDescriptorFixture" /> across every test class that joins it.
/// </summary>
[CollectionDefinition(Name)]
public class ServiceManagedDescriptorCollection : ICollectionFixture<ServiceManagedDescriptorFixture>
{
    public const string Name = "ServiceManagedDescriptor";
}
