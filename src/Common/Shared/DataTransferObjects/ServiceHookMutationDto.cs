using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Represents a service hook definition
/// </summary>
public class ServiceHookMutationDto
{
    /// <summary>
    ///     Returns true if service hook is enabled
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Enabled { get; set; }

    /// <summary>
    ///     Returns the name of service hook
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Name { get; set; }

    /// <summary>
    ///     The CK model entity id
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? QueryCkId { get; set; }

    /// <summary>
    ///     Field filters
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? FieldFilter { get; set; }

    /// <summary>
    ///     Gets or sets the base uri of the service hook service
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ServiceHookBaseUri { get; set; }

    /// <summary>
    ///     Gets or sets the service hook action
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ServiceHookAction { get; set; }

    /// <summary>
    ///     Gets or sets the service hook API key
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ServiceHookApiKey { get; set; }
}
