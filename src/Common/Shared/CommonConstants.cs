namespace Meshmakers.Octo.Common.Shared;

public static class CommonConstants
{
    [Flags]
    public enum ApiScopes
    {
        None = 0,
        AssetSystemApiFullAccess = 1,
        AssetSystemApiReadOnly = 2,
        IdentityApiFullAccess = 4,
        IdentityApiReadOnly = 8,
        BotApiFullAccess = 16,
        BotApiReadOnly = 32
    }

    [Flags]
    public enum DefaultScopes
    {
        None = 0,
        OpenId = 1,
        Profile = 2,
        Email = 4,
        Role = 8,
        OfflineAccess = 16,

        UserDefault = OpenId | Profile | Email | Role
    }

    public const string GoogleIdentityProvider = "Google";
    public const string MicrosoftIdentityProvider = "Microsoft";


    public const string IdentityApi = "identityAPI";
    public const string IdentityApiDisplayName = "Identity API";
    public const string IdentityApiDescription = "Access to user management";
    public const string IdentityApiFullAccess = "identityAPI.full_access";
    public const string IdentityApiFullAccessDisplayName = "Read and write access to user management";
    public const string IdentityApiReadOnly = "identityAPI.read_only";
    public const string IdentityApiReadOnlyDisplayName = "Read-only access to user management";

    public const string PolicyApi = "policyAPI";
    public const string PolicyApiDisplayName = "Policy API";
    public const string PolicyApiDescription = "Access to access management";
    public const string PolicyApiFullAccess = "policyAPI.full_access";
    public const string PolicyApiFullAccessDisplayName = "Read and write access to policy management";
    public const string PolicyApiReadOnly = "policyAPI.read_only";
    public const string PolicyApiReadOnlyDisplayName = "Read-only access to policy management";

    public const string SystemApi = "systemAPI";
    public const string SystemApiDisplayName = "System API";
    public const string SystemApiDescription = "Access to system management";
    public const string SystemApiFullAccess = "systemAPI.full_access";
    public const string SystemApiFullAccessDisplayName = "Read and write access to system management";
    public const string SystemApiReadOnly = "systemAPI.read_only";
    public const string SystemApiReadOnlyDisplayName = "Read-only access to system management";

    public const string BotApi = "botAPI";
    public const string BotApiDisplayName = "Bot Scheduler API";
    public const string BotApiDescription = "Bot Scheduler API Access";
    public const string BotApiFullAccess = "botAPI.full_access";
    public const string BotApiFullAccessDisplayName = "Read and write access to bot management API";
    public const string BotApiReadOnly = "botAPI.read_only";
    public const string BotApiReadOnlyDisplayName = "Read-only access to bot management API";

    public const string OctoToolClientId = "octo-cli";
    public const string OctoToolClientSecret = "{AEE2DA50-065E-4071-A56E-7B3B4B175EFB}";

    public const string OctoAdminPanelClientId = "octo-admin-panel";
    public const string OctoAdminPanelClientIdDebug = "octo-admin-panel-debug";

    public const string AssetRepositoryServicesClientId = "octo-assetRepositoryServices";
    public const string BotServicesClientId = "octo-botServices";
    public const string IdentityServicesSwaggerClientId = "octo-idenityServices-swagger";
    public const string AsserRepositoryServicesSwaggerClientId = "octo-assetRepositoryServices-swagger";
    public const string OctoBotServicesSwaggerClientId = "octo-botServices-swagger";


    public const string GraphQlConnectionSuffix = "Connection";
    public const string GraphQlEdgeSuffix = "Edge";
    public const string GraphQlUnionSuffix = "Union";
    public const string GraphQlUpdateSuffix = "Update";
    public const string GraphQlInputSuffix = "Input";
    public const string GraphQlUpdateMessageSuffix = "Message";
    public const string GraphQlDeletePrefix = "Deletion";
    public const string GraphQlUpdatePrefix = "Update";
    public const string GraphQlCreationPrefix = "Creation";

    public const string GraphQLErrorNotFound = "OCTO1000";
    public const string GraphQLErrorConflict = "OCTO1001";
    public const string GraphQLErrorInvalidType = "OCTO1002";
    public const string GraphQLErrorDataStore = "OCTO1003";
    public const string GraphQLErrorCommon = "OCTO1004";
    public const string GraphQLDeleteOperationsNotSupported = "OCTO1005";
    public const string GraphQLCkModelViolation = "OCTO1006";

    public const string AdministratorsRole = "Administrators";
    public const string ManagersRole = "Managers";
    public const string DevelopersRole = "Developers";
    public const string UsersRole = "Users";


    /// <summary>
    ///     Defines default scopes as minimal constraint
    /// </summary>
    public static readonly string[] OctoDefaultScopes =
    {
        Scopes.OpenId,
        Scopes.Profile,
        Scopes.Email,
        Scopes.Role
    };

    /// <summary>
    ///     Returns a scope definition including default scopes and api scopes
    /// </summary>
    /// <param name="apiScopes">Enum flags for API scopes.</param>
    /// <param name="scopes">Default scopes that are added </param>
    /// <returns></returns>
    public static string GetScopes(ApiScopes apiScopes, DefaultScopes scopes = DefaultScopes.UserDefault)
    {
        var list = GetDefaultScopes(scopes);

        if (apiScopes.HasFlag(ApiScopes.AssetSystemApiFullAccess))
            list.Add(SystemApiFullAccess);
        else if (apiScopes.HasFlag(ApiScopes.AssetSystemApiReadOnly)) list.Add(SystemApiReadOnly);

        if (apiScopes.HasFlag(ApiScopes.IdentityApiFullAccess))
            list.Add(IdentityApiFullAccess);
        else if (apiScopes.HasFlag(ApiScopes.IdentityApiReadOnly)) list.Add(IdentityApiReadOnly);

        if (apiScopes.HasFlag(ApiScopes.BotApiFullAccess))
            list.Add(BotApiFullAccess);
        else if (apiScopes.HasFlag(ApiScopes.BotApiReadOnly)) list.Add(BotApiReadOnly);


        return string.Join(" ", list.ToArray());
    }

    private static List<string> GetDefaultScopes(DefaultScopes scopes)
    {
        var list = new List<string>();
        if (scopes.HasFlag(DefaultScopes.OpenId)) list.Add(Scopes.OpenId);

        if (scopes.HasFlag(DefaultScopes.Profile)) list.Add(Scopes.Profile);

        if (scopes.HasFlag(DefaultScopes.Email)) list.Add(Scopes.Email);

        if (scopes.HasFlag(DefaultScopes.Role)) list.Add(Scopes.Role);

        if (scopes.HasFlag(DefaultScopes.OfflineAccess)) list.Add(Scopes.OfflineAccess);

        return list;
    }


    /// <summary>
    ///     Defines standard scopes
    /// </summary>
    public static class Scopes
    {
        /// <summary>
        ///     REQUIRED. Informs the Authorization Server that the Client is making an OpenID Connect request. If the
        ///     <c>openid</c> scope value is not present, the behavior is entirely unspecified.
        /// </summary>
        public const string OpenId = "openid";

        /// <summary>
        ///     OPTIONAL. This scope value requests access to the End-User's default profile Claims, which are: <c>name</c>,
        ///     <c>family_name</c>, <c>given_name</c>, <c>middle_name</c>, <c>nickname</c>, <c>preferred_username</c>,
        ///     <c>profile</c>, <c>picture</c>, <c>website</c>, <c>gender</c>, <c>birthdate</c>, <c>zoneinfo</c>, <c>locale</c>,
        ///     and <c>updated_at</c>.
        /// </summary>
        public const string Profile = "profile";

        /// <summary>OPTIONAL. This scope value requests access to the <c>email</c> and <c>email_verified</c> Claims.</summary>
        public const string Email = "email";

        public const string Role = "role";
        public const string Permission = "permission";
        public const string OfflineAccess = "offline_access";
    }

    public static class PermissionIds
    {
        public const string PermissionRead = "policy.permission.read";
        public const string PermissionWrite = "policy.permission.write";
        public const string PermissionRoleRead = "policy.permissionRole.read";
        public const string PermissionRoleWrite = "policy.permissionRole.write";
    }
}