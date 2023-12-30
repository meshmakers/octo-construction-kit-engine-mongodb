namespace Meshmakers.Octo.Common.Shared.Authorization;

public interface IAuthorizationClient
{
    Task<bool> IntrospectApiResource(string accessToken, string apiName, string apiSecret);

    Task<UserInfoData> GetUserInfoAsync(string accessToken);
}