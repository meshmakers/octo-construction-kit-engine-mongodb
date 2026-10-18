using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

public class OctoSystemConfiguration()
{
    private string _systemDatabaseName = "OctoSystem";
    private string _systemTenantId = "OctoSystem";

    public OctoSystemConfiguration(string databaseHost)
        : this()
    {
        DatabaseHost = databaseHost;
    }

    public string DatabaseHost { get; set; } = "localhost:27017";

    public string SystemTenantId
    {
        get => _systemTenantId;
        set
        {
            if (value == null || !Regex.IsMatch(value, ContractConstants.RegexWithoutWhitespaces))
            {
                throw ConfigurationErrorException.InvalidConfigurationValue("SystemTenantId", value);
            }

            _systemTenantId = value;
        }
    }

    public string SystemDatabaseName
    {
        get => _systemDatabaseName;
        set
        {
            if (value == null || !Regex.IsMatch(value, ContractConstants.RegexWithoutWhitespaces))
            {
                throw ConfigurationErrorException.InvalidConfigurationValue("SystemDatabaseName", value);
            }

            _systemDatabaseName = value;
        }
    }

    public string DatabaseUser { get; set; } = "octo-system-ds-user-{0}";
    public string? DatabaseUserPassword { get; set; }
    public string AdminUser { get; set; } = "octo-system-admin";
    public string? AdminUserPassword { get; set; }

    public string AuthenticationDatabaseName { get; set; } = "admin";

    /// <summary>Lowest accepted <see cref="MongoRestoreBatchSize"/>.</summary>
    public const int MinMongoRestoreBatchSize = 1;

    /// <summary>Highest accepted <see cref="MongoRestoreBatchSize"/> (the tool's own default is 1000).</summary>
    public const int MaxMongoRestoreBatchSize = 1000;

    /// <summary>Default of <see cref="MongoRestoreBatchSize"/>.</summary>
    public const int DefaultMongoRestoreBatchSize = 100;

    private int _mongoRestoreBatchSize = DefaultMongoRestoreBatchSize;

    /// <summary>
    /// Documents per bulk insert of <c>mongorestore --batchSize</c> (AB#6981). Environment variable
    /// <c>OCTO_System__MongoRestoreBatchSize</c>. Default 100, valid 1..1000; anything else fails when the
    /// configuration is bound, naming this option.
    /// </summary>
    /// <remarks>
    /// The server accepts a message of at most 48,000,000 bytes (<c>maxMessageSizeBytes</c>) and a document of at most
    /// 16 MiB (<c>maxBsonObjectSize</c>). The tool flushes a batch when the count reaches the batch size or the bytes reach
    /// 48 MB, but it checks the bytes only after it added a document, so a batch that is just below the limit plus one
    /// more document plus the message envelope is over it (mongo-tools TOOLS-4142, TOOLS-4145, TOOLS-2875). With the
    /// tool's default of 1000 documents that happens once the average document is above about 48 KB: restoring
    /// 864,070 documents of 73 KB failed with <c>msgLen 48000084 &gt; 48000000</c>. A count of 100 keeps a batch at
    /// 100 x average, i.e. below the byte limit for typical documents (averages up to about 480 KB), so the byte rule is
    /// rarely what ends a batch and the overshoot becomes unlikely. It is not a guarantee: the count bounds the number of
    /// documents, not their total size, so a batch of a few documents near the 16 MiB limit can still exceed the message
    /// limit. The cost is more round trips for small documents. Raise it for collections of tiny documents, lower it when
    /// the average document is above 480 KB or the collection holds very large documents. The figures were measured with
    /// mongorestore 100.16.0; check the version a deployment ships before relying on them.
    /// </remarks>
    public int MongoRestoreBatchSize
    {
        get => _mongoRestoreBatchSize;
        set
        {
            if (value < MinMongoRestoreBatchSize || value > MaxMongoRestoreBatchSize)
            {
                throw ConfigurationErrorException.OutOfRangeConfigurationValue(nameof(MongoRestoreBatchSize), value,
                    MinMongoRestoreBatchSize, MaxMongoRestoreBatchSize);
            }

            _mongoRestoreBatchSize = value;
        }
    }

    public bool UseTls { get; set; } = false;

    public bool AllowInsecureTls { get; set; } = true;
    
    /// <summary>
    /// When set to true, the MongoDB connection will be established directly to the database host, without using the other nodes in the replica set.
    /// </summary>
    public bool UseDirectConnection { get; set; } = false;

    /// <summary>
    /// The name of the MongoDB replica set. When set, the driver explicitly connects to the named replica set,
    /// which is required for multi-document transactions to work correctly with MongoDB.Driver v3.x.
    /// </summary>
    public string? ReplicaSetName { get; set; }

    /// <summary>
    /// Threshold in milliseconds above which a MongoDB command is logged at WARN level and tagged as slow.
    /// Set to 0 to disable slow-query logging entirely (OpenTelemetry histograms are still emitted).
    /// </summary>
    public int SlowQueryThresholdMs { get; set; } = 100;

    /// <summary>
    /// Threshold in milliseconds above which the truncated BSON command body is included in the slow-query
    /// WARN log line. Must be greater than or equal to <see cref="SlowQueryThresholdMs"/>; values below
    /// the slow-query threshold are ignored. Set to 0 to disable full-command logging.
    /// </summary>
    public int SlowQueryFullCommandLogMs { get; set; } = 1000;

    /// <summary>
    /// Maximum length in bytes of the BSON command preview attached to slow-query log entries.
    /// Prevents huge aggregation pipelines from flooding the log.
    /// </summary>
    public int SlowQueryCommandPreviewBytes { get; set; } = 2048;

    /// <summary>
    /// Number of slow-query entries the per-service in-memory ring buffer retains for the
    /// Refinery Studio Diagnostics surface. Default 1000, set to 0 to disable buffer capture
    /// (metrics and slow-log still fire). At ~3 KB per entry the default is ~3 MB resident.
    /// </summary>
    public int SlowQueryBufferSize { get; set; } = 1000;

    /// <summary>
    /// Master switch for the asynchronous <c>explain()</c> capture of slow queries (Stage 2B
    /// Performance Advisor). When <c>false</c>, no explain probe is ever scheduled and the
    /// cache stays empty. Existing slow-query metrics / log / buffer behaviour is unaffected.
    /// </summary>
    public bool SlowQueryExplainEnabled { get; set; } = true;

    /// <summary>
    /// Minimum seconds between explain captures for the same
    /// <c>(Fingerprint, CommandName, Target, Database)</c> key. Within this window a slow
    /// query that already has a cached explain does not trigger a new round-trip. Default 300 s.
    /// </summary>
    public int SlowQueryExplainCooldownSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum distinct keys held in the explain cache. On overflow the oldest entry is
    /// FIFO-evicted (Interlocked counter, same pattern as <c>SlowQueriesBuffer</c>).
    /// Default 5000; at ~5 KB per stored preview that bounds resident memory at ~25 MB.
    /// </summary>
    public int SlowQueryExplainCacheCapacity { get; set; } = 5000;

    /// <summary>
    /// Per-explain wall-clock budget in seconds. The MongoDB driver call gets a
    /// CancellationToken derived from this; on timeout the cache records
    /// <c>Status=failed</c> with <c>ErrorMessage=&quot;timeout&quot;</c>. Default 5 s.
    /// </summary>
    public int SlowQueryExplainTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// UTF-8 byte cap on the truncated raw <c>queryPlanner</c> JSON stored on each
    /// <c>SlowQueryExplain</c>. Default 4096; matches the order-of-magnitude of
    /// <c>SlowQueryCommandPreviewBytes</c> so neither half of the entry dominates.
    /// </summary>
    public int SlowQueryExplainPreviewBytes { get; set; } = 4096;
}