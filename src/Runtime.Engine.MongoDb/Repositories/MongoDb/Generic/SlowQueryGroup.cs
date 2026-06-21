namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Aggregated slow-query group — one row per structural fingerprint, summarising every
/// matching <see cref="SlowQueryEntry"/> in the buffer. Surfaces in the Refinery Studio
/// Diagnostics page when the user toggles "Group similar", so that a hot endpoint producing
/// dozens of structurally-identical slow queries shows up as a single line with an occurrence
/// count instead of dozens of rows.
/// </summary>
/// <param name="Fingerprint">16-char hex fingerprint shared by all entries in this group.</param>
/// <param name="CommandName">Driver-level command name shared by all entries (e.g. <c>aggregate</c>).</param>
/// <param name="Target">Collection / target shared by all entries (e.g. <c>rt_entities</c>).</param>
/// <param name="Database">Database (tenant ID) shared by all entries.</param>
/// <param name="Count">Number of entries with this fingerprint.</param>
/// <param name="FirstSeen">UTC timestamp of the earliest entry.</param>
/// <param name="LastSeen">UTC timestamp of the most recent entry.</param>
/// <param name="MinDurationMs">Fastest observed duration in this group.</param>
/// <param name="MaxDurationMs">Slowest observed duration in this group.</param>
/// <param name="AvgDurationMs">Mean duration across the group.</param>
/// <param name="Representative">Most-recent matching entry — carries the truncated BSON preview for inspection.</param>
public sealed record SlowQueryGroup(
    string Fingerprint,
    string CommandName,
    string Target,
    string Database,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double MinDurationMs,
    double MaxDurationMs,
    double AvgDurationMs,
    SlowQueryEntry Representative);
