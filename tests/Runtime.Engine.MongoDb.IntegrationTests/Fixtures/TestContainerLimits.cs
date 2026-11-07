using Docker.DotNet.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Memory caps for the Testcontainers this suite starts (AB#8511).
/// </summary>
/// <remarks>
///     <para>
///         Without a cap a container is sized from the Docker host. On the CI node (one 125 GiB host, every
///         agent's Docker-in-Docker containers in one shared cgroup) mongod then sized its WiredTiger cache
///         to ~62 GB and grew with the suite; when several agents ran Testcontainers suites at once the
///         shared cgroup hit its <c>memory.high</c> and the kernel throttled all of them together. Tests
///         stalled for minutes, MongoDB aborted transactions after its 60 s lifetime limit and CrateDB DDL
///         ran past its 30 s budget.
///     </para>
///     <para>
///         The caps keep one suite inside a fixed, small footprint. They are not a reason to raise
///         <c>transactionLifetimeLimitSeconds</c> or any test budget: a test that needs more is a finding,
///         not something to hide.
///     </para>
/// </remarks>
internal static class TestContainerLimits
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    ///     WiredTiger cache of the shared mongod, in GB (<c>--wiredTigerCacheSizeGB</c>). The suite's data is small;
    ///     without the flag mongod takes half of the memory it sees (about 62 GB on the CI node).
    /// </summary>
    public const string MongoWiredTigerCacheSizeGb = "1";

    /// <summary>
    ///     Container memory limit of the shared mongod. Measured 2026-11-07 (full suite, 1,082 tests, cache 1 GB,
    ///     cgroup <c>memory.peak</c>): 7.1, 6.4 and 8.4 GiB in three runs, almost all of it outside the cache: every
    ///     fixture keeps its databases in the shared server (~70,000-106,000 open WiredTiger data handles, up to ~800
    ///     connections). A 3 GiB limit was OOM-killed in the middle of the run. 12 GiB leaves ~40 % headroom over the
    ///     highest peak; when the suite outgrows it the container is OOM-killed and the tests fail with "Connection
    ///     refused" - raise the limit with a new measurement, or let fixtures drop their databases.
    /// </summary>
    public const long MongoMemoryLimitBytes = 12 * GiB;

    /// <summary>
    ///     CrateDB JVM heap (<c>CRATE_HEAP_SIZE</c>).
    /// </summary>
    public const string CrateHeapSize = "1g";

    /// <summary>
    ///     Container memory limit of a CrateDB test container: the 1 GB heap plus off-heap memory and Lucene's file
    ///     cache. Measured 2026-11-07: peak 1.9 GiB (staging copy fixture) and 1.1 GiB (blueprint archive fixture).
    /// </summary>
    public const long CrateMemoryLimitBytes = 3 * GiB;

    /// <summary>
    ///     Returns a create-parameter modifier that sets a hard memory limit without swap.
    /// </summary>
    public static Action<CreateContainerParameters> MemoryLimit(long bytes)
    {
        return parameters =>
        {
            parameters.HostConfig ??= new HostConfig();
            parameters.HostConfig.Memory = bytes;
            // Same value as Memory: no swap on top, so the limit is the real ceiling.
            parameters.HostConfig.MemorySwap = bytes;
        };
    }
}
