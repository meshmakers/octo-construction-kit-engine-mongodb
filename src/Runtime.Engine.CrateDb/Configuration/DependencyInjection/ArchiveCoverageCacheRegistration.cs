using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration.DependencyInjection;

/// <summary>
/// Registers the process-wide <see cref="ArchiveCoverageCache"/> singleton (AB#5157) — shared by
/// both stream-data registration entry points so the tenant contexts, the lifecycle service and
/// the recompute orchestrator all invalidate the same memo.
/// </summary>
internal static class ArchiveCoverageCacheRegistration
{
    /// <summary>
    /// Adds the cache as <see cref="ArchiveCoverageCache"/> and as
    /// <see cref="IArchiveCoverageInvalidator"/> (same instance). The TTL comes from
    /// <see cref="ArchiveCoverageOptions"/> when a host bound them, else
    /// <see cref="ArchiveCoverageCache.DefaultCacheTtl"/>. A bound but negative TTL is a
    /// misconfiguration and surfaces as an <see cref="OptionsValidationException"/> rather than
    /// silently reverting to the default. Try-add semantics: a host that registers its own cache
    /// first keeps it.
    /// </summary>
    /// <exception cref="OptionsValidationException">
    /// The host bound <c>StreamData:Coverage:CacheTtlSeconds</c> to a negative value.
    /// </exception>
    internal static IServiceCollection AddArchiveCoverageCache(this IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            // No options registration at all (section absent and nothing bound in code) — default.
            var configured = provider.GetService<IOptions<ArchiveCoverageOptions>>()?.Value.CacheTtlSeconds;
            if (configured is not { } seconds)
            {
                return new ArchiveCoverageCache(ArchiveCoverageCache.DefaultCacheTtl);
            }

            if (seconds < 0)
            {
                throw new OptionsValidationException(
                    Options.DefaultName,
                    typeof(ArchiveCoverageOptions),
                    [
                        $"'{ArchiveCoverageOptions.SectionName}:{nameof(ArchiveCoverageOptions.CacheTtlSeconds)}' must not be negative, but was {seconds}."
                    ]);
            }

            return new ArchiveCoverageCache(TimeSpan.FromSeconds(seconds));
        });
        services.TryAddSingleton<IArchiveCoverageInvalidator>(provider => provider.GetRequiredService<ArchiveCoverageCache>());
        return services;
    }
}
