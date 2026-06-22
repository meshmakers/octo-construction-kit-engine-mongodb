using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="CkModelImportMigrationFixture" /> across every test class that joins
///     it. Replaces per-class <c>IClassFixture&lt;CkModelImportMigrationFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class CkModelImportMigrationCollection : ICollectionFixture<CkModelImportMigrationFixture>
{
    public const string Name = "CkModelImportMigration";
}
