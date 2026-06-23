using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="ImportTestCkModelFixture" /> (with the test CK model already imported)
///     across every test class that joins it. Replaces per-class
///     <c>IClassFixture&lt;ImportTestCkModelFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class ImportTestCkModelCollection : ICollectionFixture<ImportTestCkModelFixture>
{
    public const string Name = "ImportTestCkModel";
}
