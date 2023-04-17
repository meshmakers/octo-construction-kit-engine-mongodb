using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

/// <summary>
///     Base class for all specific identity provider configuration classes.
/// </summary>
[BsonKnownTypes(typeof(AzureAdIdentityProvider), typeof(GoogleIdentityProvider),
    typeof(MicrosoftIdentityProvider), typeof(MicrosoftAdIdentityProvider), 
    typeof(OpenLdapIdentityProvider))]
[CollectionName("IdentityProviders")]
public class OctoIdentityProvider
{
    /// <summary>
    ///     The key for the identity provider type as represented in the JSON.
    /// </summary>
    public const string TypeDataMemberName = "type";

    /// <summary>
    ///     Indicates if an identity provider is enabled
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    ///     Unique ID for the IdentityProviderConfiguration. Do not set this property when creating a new configuration.
    ///     The API automatically returns an ID once the configuration has been created.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    /// <summary>
    ///     The source type of the identity provider (e.g. AzureAD, OpenLDAP ...).
    /// </summary>
    public IdentityProviderTypes Type { get; set; }

    /// <summary>
    ///     Free definable for all different identity provider types.
    /// </summary>
    public string Alias { get; set; }

    /// <summary>
    ///     An arbitrary long text describing the identity provider configuration in detail.
    /// </summary>
    public string Description { get; set; }
}
