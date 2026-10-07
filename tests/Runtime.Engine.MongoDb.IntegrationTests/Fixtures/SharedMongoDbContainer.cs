using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Configuration;

using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Experiment: one MongoDB replica-set Testcontainer for the whole test process instead of one
///     per fixture. Every <see cref="DatabaseFixture" />-derived fixture now gets its own
///     <see cref="ConfigurationFixture.SystemDatabaseName" /> (GUID-suffixed), so they no longer need
///     a private server to avoid colliding on the same database — they can share the one server this
///     class starts on first use. Never stopped explicitly; Testcontainers' Ryuk reaper cleans it up
///     when the test process exits.
/// </summary>
internal static class SharedMongoDbContainer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _host;

    public static async Task<string> GetHostAsync(SystemTestOptions options)
    {
        if (_host != null)
        {
            return _host;
        }

        await Gate.WaitAsync();
        try
        {
            if (_host != null)
            {
                return _host;
            }

            // Same retry rationale as the former per-fixture start: Testcontainers' rs.initiate()
            // handshake races with mongod startup and occasionally hits "container is not running"
            // / Docker 409 Conflict under load.
            const int maxAttempts = 3;
            var perAttemptTimeout = TimeSpan.FromMinutes(2);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var container = new MongoDbBuilder(options.MongoDbImage)
                    .WithReplicaSet()
                    .WithName($"mongodb-test-shared-{Guid.NewGuid():N}")
                    .WithUsername(options.AdminUser)
                    .WithPassword(options.AdminUserPassword)
                    .Build();

                using var startCts = new CancellationTokenSource(perAttemptTimeout);
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await container.StartAsync(startCts.Token);
                    _host = $"localhost:{container.GetMappedPublicPort()}";
                    // Reported, not Console.WriteLine'd: console output from a fixture is captured as
                    // test output and only ever reaches the .trx, which is why this line was invisible
                    // in every CI log so far (AB#5436).
                    RunProgress.Report(
                        $"shared Testcontainer MongoDB ready at {_host} after {RunProgress.Seconds(watch.Elapsed)}s");
                    break;
                }
                catch (Exception ex)
                {
                    RunProgress.Report(
                        $"shared Testcontainer MongoDB start failed on attempt {attempt}/{maxAttempts}: {ex.GetType().Name}: {ex.Message}");

                    try
                    {
                        await container.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        RunProgress.Report($"disposal of the failed container also threw: {disposeEx.Message}");
                    }

                    if (attempt == maxAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            return _host!;
        }
        finally
        {
            Gate.Release();
        }
    }
}
