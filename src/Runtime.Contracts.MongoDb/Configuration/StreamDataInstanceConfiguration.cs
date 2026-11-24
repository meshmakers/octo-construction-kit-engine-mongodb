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

    /// <summary>
    /// Most sub-batch statements (1,000 rows each) one archive insert runs at the same time (AB#8875). The rows are split
    /// into lanes by row key, so two statements in flight never carry the same key; each statement leases its own
    /// connection, so this is the share of the tenant's pool one large write may hold. Default 4. Key
    /// <c>StreamData:MaxConcurrentInsertStatements</c> (environment variable
    /// <c>OCTO_STREAMDATA__MAXCONCURRENTINSERTSTATEMENTS</c>). Not positive = 1 (one statement after another); above the
    /// pool size of the stream data client = that pool size.
    /// </summary>
    public int MaxConcurrentInsertStatements { get; set; } = 4;

    /// <summary>
    /// Connections per tenant in the CrateDB pool of the stream data client (AB#8875). Key <c>StreamData:MaxPoolSize</c>
    /// (environment variable <c>OCTO_STREAMDATA__MAXPOOLSIZE</c>). A positive value overrides the pool size the host
    /// sets on its stream data configuration; unset, zero or negative keeps the host's value (default 32).
    /// </summary>
    public int? MaxPoolSize { get; set; }
}
