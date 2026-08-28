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

    /// <summary>
    /// Optional CrateDB schema instance prefix (AB#4946, Epic AB#4944). When set, every tenant
    /// schema is named <c>{prefix}_{tenantId}</c> so two OctoMesh instances can share one CrateDB
    /// cluster without colliding on identical tenant ids. DEFAULT EMPTY — existing instances must
    /// keep this unset or their tenants' schemas would move; deliberately a separate setting and
    /// NOT derived from the RabbitMQ <c>instancePrefix</c>, which existing deployments (e.g.
    /// test-2 with prefix <c>main</c>) already set while their CrateDB schemas are un-prefixed.
    /// Lives on the root <c>StreamData</c> section, so the fixed env var
    /// <c>OCTO_STREAMDATA__SCHEMAINSTANCEPREFIX</c> reaches every service uniformly. Cleaned to
    /// lowercase alphanumeric; applied process-wide and set-once
    /// (<c>TenantSchema.SetInstancePrefix</c>).
    /// </summary>
    public string? SchemaInstancePrefix { get; set; }
}
