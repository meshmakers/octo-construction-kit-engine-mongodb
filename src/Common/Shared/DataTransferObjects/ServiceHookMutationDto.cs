using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Represents a service hook definition
/// </summary>
public class ServiceHookMutationDto
{
    /// <summary>
    ///     Returns true if service hook is enabled
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool? Enabled { get; set; }

    /// <summary>
    ///     Returns the name of service hook
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Name { get; set; }

    /// <summary>
    ///     The CK model entity id
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? QueryCkTypeId { get; set; }

    /// <summary>
    ///     Field filters
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? FieldFilter { get; set; }

    /// <summary>
    ///     Gets or sets the base uri of the service hook service
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ServiceHookBaseUri { get; set; }

    /// <summary>
    ///     Gets or sets the service hook action
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ServiceHookAction { get; set; }

    /// <summary>
    ///     Gets or sets the service hook API key
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ServiceHookApiKey { get; set; }
}