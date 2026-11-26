// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     What the runtime read guard does with an unbounded runtime-entity read.
/// </summary>
public enum RuntimeReadGuardMode
{
    /// <summary>Nothing is logged or counted.</summary>
    Off,

    /// <summary>One warning per execution and node and one count of the metric; the read is not changed.</summary>
    Warn
}

/// <summary>
///     The runtime read guard of a host that reads runtime entities directly through the engine (pipeline nodes in the
///     mesh adapter). Bound from the host's <c>"RuntimeReadGuard"</c> configuration section. Active only in a host that
///     registers it with <c>AddRuntimeReadGuard</c>; the asset repository does not, because its API has a guard of its
///     own, so a read is never counted twice.
/// </summary>
/// <remarks>
///     A read is unbounded when it may return more than <see cref="MaxTake" /> entities: no <c>take</c>, or a
///     <c>take</c> above <see cref="MaxTake" /> (<c>int.MaxValue</c> included), unless a key list of at most
///     <see cref="MaxTake" /> ids bounds it. The guard never caps or refuses a read: results, paging and errors are the
///     same in every mode.
/// </remarks>
public sealed class RuntimeReadGuardOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "RuntimeReadGuard";

    /// <summary>The default of <see cref="MaxTake" />, the page limit of the asset repository's query guard.</summary>
    public const int DefaultMaxTake = 1000;

    /// <summary>
    ///     What the guard does: the name of a <see cref="RuntimeReadGuardMode" /> (<c>Off</c> or <c>Warn</c>, case
    ///     ignored). Default <c>Warn</c>. Text rather than the enum, so that a wrong value does not fail the binding
    ///     of the whole section: the guard logs it once as a warning and runs as <c>Warn</c>.
    /// </summary>
    public string? Mode { get; set; } = nameof(RuntimeReadGuardMode.Warn);

    /// <summary>
    ///     <see cref="Mode" /> as a <see cref="RuntimeReadGuardMode" />, or false when it names none (empty, unknown, a
    ///     number or a list).
    /// </summary>
    internal bool TryGetMode(out RuntimeReadGuardMode mode)
    {
        mode = RuntimeReadGuardMode.Warn;
        var text = Mode?.Trim();
        // Only a plain name: Enum.TryParse would also take numbers and comma lists ("Off,Warn").
        if (string.IsNullOrEmpty(text) || !text.All(char.IsLetter))
        {
            return false;
        }

        return Enum.TryParse(text, true, out mode) && Enum.IsDefined(mode);
    }

    /// <summary>
    ///     The largest <c>take</c> that counts as bounded. Default <see cref="DefaultMaxTake" />; a value below 1 is
    ///     treated as the default.
    /// </summary>
    public int MaxTake { get; set; } = DefaultMaxTake;

    /// <summary>The effective limit.</summary>
    internal int EffectiveMaxTake => MaxTake > 0 ? MaxTake : DefaultMaxTake;
}
