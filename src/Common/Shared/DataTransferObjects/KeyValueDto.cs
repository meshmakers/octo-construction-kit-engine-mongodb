using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Key Value data transfer object
/// </summary>
public class KeyValueDto
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="claim">A security claim</param>
    public KeyValueDto(Claim claim)
    {
        Key = claim.Type;
        Value = claim.Value;
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="keyValuePair">A key value pair</param>
    public KeyValueDto(KeyValuePair<string, string> keyValuePair)
    {
        Key = keyValuePair.Key;
        Value = keyValuePair.Value;
    }

    /// <summary>
    ///     Returns the key
    /// </summary>
    [Required]
    public string Key { get; }

    /// <summary>
    ///     Return the value
    /// </summary>
    public string Value { get; }
}