namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Error codes when the Identity API encounters an error
/// </summary>
public enum ErrorResponseCode
{
    /// <summary>
    ///     The specified parameters were invalid.
    /// </summary>
    ParametersInvalid = 1,

    /// <summary>
    ///     An attempt was made to introduce a value
    ///     that already exists but must be unqiue for the same resource.
    /// </summary>
    ValueNotUnique = 2
}
