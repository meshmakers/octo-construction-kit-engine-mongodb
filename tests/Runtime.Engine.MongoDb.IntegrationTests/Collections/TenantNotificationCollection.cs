using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the order of the tenant-created notification. See
///     <see cref="TenantNotificationFixture" />.
/// </summary>
[CollectionDefinition(Name)]
public class TenantNotificationCollection : ICollectionFixture<TenantNotificationFixture>
{
    public const string Name = "TenantNotification";
}
