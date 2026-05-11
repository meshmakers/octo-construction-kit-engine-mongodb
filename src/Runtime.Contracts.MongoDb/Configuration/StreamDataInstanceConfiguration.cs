namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

/// <summary>
/// Instance-level (process-wide) StreamData kill switch (concept §5). When
/// <see cref="Enabled"/> is <c>false</c> no tenant can activate StreamData and the CrateDB stack
/// stays out of the runtime path entirely. Bound from the <c>StreamData</c> appsettings section
/// by <c>AddCrateDbStreamDataRepository</c>.
/// </summary>
public class StreamDataInstanceConfiguration
{
    /// <summary>
    /// Configuration section name read from <c>appsettings.json</c>.
    /// </summary>
    public const string SectionName = "StreamData";

    /// <summary>
    /// Whether StreamData is enabled at the instance level. Defaults to <c>false</c> so the
    /// feature is opt-in; deployments that want StreamData must set <c>StreamData:Enabled</c> to
    /// <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; }
}
