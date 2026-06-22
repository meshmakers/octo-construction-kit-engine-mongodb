using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one bare <see cref="DatabaseFixture" /> (Testcontainer only, no system tenant) across
///     every test class that joins it. Replaces per-class
///     <c>IClassFixture&lt;DatabaseFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}
