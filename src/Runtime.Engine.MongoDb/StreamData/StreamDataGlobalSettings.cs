namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Represents whether stream data is enabled for a tenant.
/// Stored in tenant configuration — must not be in the StreamData CK model
/// because the CK may not be imported yet.
/// </summary>
public class StreamDataGlobalSettings
{
    public static StreamDataGlobalSettings Enabled => new() { IsEnabled = true };
    public static StreamDataGlobalSettings Disabled => new() { IsEnabled = false };

    /// <summary>
    /// Whether stream data is enabled for the tenant.
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Configuration keys for stream data.
/// </summary>
public static class StreamDataConfigurationKeys
{
    /// <summary>
    /// Tenant configuration key for the stream data enabled flag.
    /// </summary>
    public const string StreamDataEnabledKey = "StreamDataEnabled";
}
