using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Generic error response
/// </summary>
public class ErrorResponse
{
    /// <summary>
    ///     The key for the error code as represented in the JSON.
    /// </summary>
    public const string ErrorCodeJsonName = "errorCode";

    /// <summary>
    ///     Error message that informs about the error.
    /// </summary>
    [Required]
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Error code that determines the type of error.
    /// </summary>
    [Required]
    public ErrorResponseCode ErrorCode { get; set; }

    /// <summary>
    ///     Parameter validation errors (multiple errors per parameter are possible).
    /// </summary>
    public Dictionary<string, List<string>>? Errors { get; set; }
}