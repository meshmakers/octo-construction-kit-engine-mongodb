using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Limits of the interface query (AB#7019, decision of spike AB#6930, 2026-10-19).
/// </summary>
public static class RtInterfaceQueryLimits
{
    /// <summary>
    ///     Maximum number of collection roots one interface query fans out to. The same cap as the abstract
    ///     navigation target (AB#5000): a stock tenant has about 38 roots.
    /// </summary>
    public const int MaxCollectionRoots = 64;

    /// <summary>
    ///     Maximum of <c>skip + take</c>. Every collection returns its first <c>skip + take</c> rows to the merge, so
    ///     the work grows with the number of collections times this value.
    /// </summary>
    public const int MaxSkipPlusTake = 1000;
}

/// <summary>
///     The stable reason tokens of <see cref="InterfaceQueryException" /> (AB#7019). Code and parameters are the
///     contract, the message text is not.
/// </summary>
public static class InterfaceQueryReasons
{
    /// <summary>The interface does not exist in the tenant's CK cache. Parameters: <c>ckInterfaceId</c>.</summary>
    public const string UnknownInterface = "UnknownInterface";

    /// <summary>A text search (full text or attribute search) was requested. Parameters: <c>ckInterfaceId</c>.</summary>
    public const string TextSearchNotSupported = "TextSearchNotSupported";

    /// <summary>A field or result aggregation was requested. Parameters: <c>ckInterfaceId</c>.</summary>
    public const string AggregationNotSupported = "AggregationNotSupported";

    /// <summary>
    ///     A navigation lookup (a filter or sort path through an association, <c>-&gt;</c> or <c>::</c>) or a geospatial
    ///     filter was requested. Parameters: <c>ckInterfaceId</c>, <c>attributePath</c> (empty for geospatial).
    /// </summary>
    public const string NavigationNotSupported = "NavigationNotSupported";

    /// <summary>
    ///     The interface is implemented in more collection roots than the cap allows. Parameters:
    ///     <c>ckInterfaceId</c>, <c>rootCount</c>, <c>maxRoots</c>.
    /// </summary>
    public const string TooManyCollectionRoots = "TooManyCollectionRoots";

    /// <summary>
    ///     <c>skip + take</c> is above the cap. Parameters: <c>skip</c>, <c>take</c>, <c>maxSkipPlusTake</c>.
    /// </summary>
    public const string PageTooLarge = "PageTooLarge";

    /// <summary>
    ///     A filter or sort path does not start with a member of the interface. Parameters: <c>ckInterfaceId</c>,
    ///     <c>attributePath</c>, <c>usage</c> (<c>filter</c> or <c>sort</c>).
    /// </summary>
    public const string UnknownMember = "UnknownMember";
}

/// <summary>
///     A request to <see cref="Repositories.ITenantRepository.GetRtEntitiesByInterfaceAsync" /> that the interface
///     query does not support or that exceeds its limits (AB#7019). The stable contract is
///     <see cref="MessageNumber" />, <see cref="Reason" /> and <see cref="Parameters" />; the text of
///     <see cref="Exception.Message" /> is English for logs and may be reworded.
/// </summary>
[Serializable]
public sealed class InterfaceQueryException : PersistenceException
{
    /// <summary>The message number of every interface query refusal (the work item, AB#7019).</summary>
    public const int InterfaceQueryMessageNumber = 7019;

    private InterfaceQueryException(OperationMessage message, string reason) : base(message.MessageText)
    {
        Details = message;
        Reason = reason;
    }

    /// <summary>The message with number, text and parameters (the structured error contract of the engine).</summary>
    public OperationMessage Details { get; }

    /// <summary>The stable message number, always <see cref="InterfaceQueryMessageNumber" />.</summary>
    public int MessageNumber => Details.MessageNumber;

    /// <summary>The stable reason token, one of <see cref="InterfaceQueryReasons" />.</summary>
    public string Reason { get; }

    /// <summary>The machine-readable parameters of the refusal, by name, invariant culture.</summary>
    public IReadOnlyDictionary<string, string> Parameters => Details.Parameters;

    private static InterfaceQueryException Create(string reason, string text,
        params (string Name, string Value)[] parameters)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason };
        foreach (var (name, value) in parameters)
        {
            dictionary[name] = value;
        }

        return new InterfaceQueryException(
            new OperationMessage(MessageLevel.Error, null, InterfaceQueryMessageNumber, text, dictionary), reason);
    }

    internal static InterfaceQueryException UnknownInterface(RtCkId<CkInterfaceId> ckInterfaceId) =>
        Create(InterfaceQueryReasons.UnknownInterface, $"The CK interface '{ckInterfaceId}' does not exist.",
            ("ckInterfaceId", ckInterfaceId.ToString()));

    internal static InterfaceQueryException TextSearchNotSupported(RtCkId<CkInterfaceId> ckInterfaceId) =>
        Create(InterfaceQueryReasons.TextSearchNotSupported,
            $"Text search is not supported by the query of interface '{ckInterfaceId}'.",
            ("ckInterfaceId", ckInterfaceId.ToString()));

    internal static InterfaceQueryException AggregationNotSupported(RtCkId<CkInterfaceId> ckInterfaceId) =>
        Create(InterfaceQueryReasons.AggregationNotSupported,
            $"Aggregations are not supported by the query of interface '{ckInterfaceId}'.",
            ("ckInterfaceId", ckInterfaceId.ToString()));

    internal static InterfaceQueryException NavigationNotSupported(RtCkId<CkInterfaceId> ckInterfaceId,
        string attributePath) =>
        Create(InterfaceQueryReasons.NavigationNotSupported,
            $"Navigation lookups and geospatial filters are not supported by the query of interface '{ckInterfaceId}' " +
            $"(attribute path '{attributePath}').",
            ("ckInterfaceId", ckInterfaceId.ToString()), ("attributePath", attributePath));

    internal static InterfaceQueryException TooManyCollectionRoots(RtCkId<CkInterfaceId> ckInterfaceId, int rootCount,
        int maxRoots) =>
        Create(InterfaceQueryReasons.TooManyCollectionRoots,
            $"Interface '{ckInterfaceId}' is implemented in {rootCount} entity collections; at most {maxRoots} are supported.",
            ("ckInterfaceId", ckInterfaceId.ToString()),
            ("rootCount", rootCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("maxRoots", maxRoots.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    internal static InterfaceQueryException PageTooLarge(int skip, int take, int maxSkipPlusTake) =>
        Create(InterfaceQueryReasons.PageTooLarge,
            $"skip ({skip}) + take ({take}) is above the maximum of {maxSkipPlusTake} of an interface query.",
            ("skip", skip.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("take", take.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("maxSkipPlusTake", maxSkipPlusTake.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    internal static InterfaceQueryException UnknownMember(RtCkId<CkInterfaceId> ckInterfaceId, string attributePath,
        string usage) =>
        Create(InterfaceQueryReasons.UnknownMember,
            $"'{attributePath}' is not a member of interface '{ckInterfaceId}' and cannot be used to {usage}.",
            ("ckInterfaceId", ckInterfaceId.ToString()), ("attributePath", attributePath), ("usage", usage));
}
