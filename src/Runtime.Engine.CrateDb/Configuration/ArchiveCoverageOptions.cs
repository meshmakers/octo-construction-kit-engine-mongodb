namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;

/// <summary>
/// Host-bound tunables of the process-wide archive coverage memo
/// (<c>ArchiveCoverageCache</c>, AB#5157). Hosts bind it from the <see cref="SectionName"/>
/// configuration section; when nothing is bound the cache falls back to
/// <c>ArchiveCoverageCache.DefaultCacheTtl</c>.
/// </summary>
public sealed class ArchiveCoverageOptions
{
    /// <summary>Configuration section hosts bind these options from.</summary>
    public const string SectionName = "StreamData:Coverage";

    /// <summary>
    /// How long a measured coverage answer is served without re-probing the storage layer, in
    /// seconds. Zero disables memoisation (every request measures). Default 60.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 60;
}
