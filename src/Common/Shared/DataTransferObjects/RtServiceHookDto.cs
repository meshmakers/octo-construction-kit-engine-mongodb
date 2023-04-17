using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Represents a service hook definition
/// </summary>
public class RtServiceHookDto
{
    /// <summary>
    ///     Returns the unique key of the service hook
    /// </summary>
    [JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Returns true if service hook is enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Returns the name of service hook
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     The CK model entity id
    /// </summary>
    public string? QueryCkId { get; set; }

    /// <summary>
    ///     Field filters
    /// </summary>
    public string? FieldFilter { get; set; }

    /// <summary>
    ///     Gets or sets the base uri of the service hook service
    /// </summary>
    public string? ServiceHookBaseUri { get; set; }

    /// <summary>
    ///     Gets or sets the service hook action
    /// </summary>
    public string? ServiceHookAction { get; set; }

    /// <summary>
    ///     Gets or sets the service hook API key
    /// </summary>
    public string? ServiceHookApiKey { get; set; }
}
