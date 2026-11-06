namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Fixture for <c>SystemModelMismatchTests</c>.
/// </summary>
/// <remarks>
///     Its test rewrites the System CK model of its own system tenant to a version this process was
///     not compiled against (AB#5492) and leaves it that way, so the system tenant is unusable for
///     anyone else afterwards — it needs a system database nobody shares.
/// </remarks>
public class SystemModelMismatchFixture : SystemFixture;
