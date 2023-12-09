namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Returned when the creation or update of a resource
///     would have lead to a unique constraint on a field to be violated.
/// </summary>
public class UniquenessViolationErrorResponse : ErrorResponse
{
    /// <summary>
    ///     List of fields whose unique constraint would be violated by the operation.
    /// </summary>
    public List<string>? ViolatedUniqueFields { get; set; }
}