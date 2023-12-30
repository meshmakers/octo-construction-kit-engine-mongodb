using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using static Meshmakers.Octo.Common.Shared.DataTransferObjects.ValidationConstants;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Data Transfer Object of a Octo identity provider
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = TypeJsonName)]
[JsonDerivedType(typeof(GoogleIdentityProviderDto), (int)IdentityProviderTypesDto.Google)]
[JsonDerivedType(typeof(MicrosoftIdentityProviderDto), (int)IdentityProviderTypesDto.Microsoft)]
[JsonDerivedType(typeof(AzureEntraProviderDto), (int)IdentityProviderTypesDto.MicrosoftAzureAd)]
[JsonDerivedType(typeof(MicrosoftAdProviderDto), (int)IdentityProviderTypesDto.MicrosoftActiveDirectory)]
[JsonDerivedType(typeof(OpenLdapProviderDto), (int)IdentityProviderTypesDto.OpenLdap)]
public class IdentityProviderDto
{
    /// <summary>
    ///     The key for the identity provider type as represented in the JSON.
    /// </summary>
    public const string TypeJsonName = "type";

    /// <summary>
    ///     The source type of the identity provider (e.g. AzureAD, OpenLDAP ...).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [Required]
    [JsonPropertyName(TypeJsonName)]
    [JsonPropertyOrder(-5)]
    public IdentityProviderTypesDto Type { get; set; }

    /// <summary>
    ///     Indicates if an identity provider is enabled
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public bool IsEnabled { get; set; }

    /// <summary>
    ///     Unique ID for the IdentityProviderConfiguration. Do not set this property when creating a new configuration.
    ///     The API automatically returns an ID once the configuration has been created.
    /// </summary>
    [StringLength(TextDefaultMaxLength)]
    public string? Id { get; set; }

    /// <summary>
    ///     Free definable for all different identity provider types.
    /// </summary>
    [Required]
    [StringLength(AliasMaxLength, MinimumLength = AliasMinLength)]
    public string? Alias { get; set; }

    /// <summary>
    ///     An arbitrary long text describing the identity provider configuration in detail.
    /// </summary>
    [StringLength(DescriptionDefaultMaxLength)]
    public string? Description { get; set; }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

    public const int AliasMinLength = 3;
    public const int AliasMaxLength = TextDefaultMaxLength;

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}