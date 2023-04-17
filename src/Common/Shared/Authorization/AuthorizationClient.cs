using System;
using System.Net.Http;
using System.Threading.Tasks;
using IdentityModel.Client;
using Meshmakers.Common.Shared;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Common.Shared.Authorization;

public class AuthorizationClient : IAuthorizationClient
{
    private IDiscoveryCache? _cache;
    
    public AuthorizationClient(IOptionsMonitor<AuthorizationOptions> options)
    {
        Options = options.CurrentValue;
        
        options.OnChange(CreateCache);
        if (!string.IsNullOrWhiteSpace(options.CurrentValue.IssuerUri))
        {
            CreateCache(options.CurrentValue);
        }
    }

    private IDiscoveryCache Cache
    {
        get
        {
            if (_cache == null)
            {
                throw new ServiceConfigurationMissingException("Discovery cache not initialized.");
            }
            return _cache;
        }
    }
    
    protected AuthorizationOptions Options { get; private set; }

    public async Task<UserInfoData> GetUserInfoAsync(string accessToken)
    {
        ArgumentValidation.ValidateString(nameof(accessToken), accessToken);

        var disco = await GetDiscoveryResponse();

        var client = new HttpClient();

        var response = await client.GetUserInfoAsync(new UserInfoRequest
        {
            Address = disco.UserInfoEndpoint,
            Token = accessToken
        });

        if (response.IsError)
        {
            return new UserInfoData(false, null);
        }

        return new UserInfoData(true, response.Claims);
    }

    public async Task<bool> IntrospectApiResource(string accessToken, string apiName, string apiSecret)
    {
        ArgumentValidation.ValidateString(nameof(accessToken), accessToken);
        ArgumentValidation.ValidateString(nameof(apiName), apiName);
        ArgumentValidation.ValidateString(nameof(apiSecret), apiSecret);

        var disco = await GetDiscoveryResponse();

        var client = new HttpClient();
        var result = await client.IntrospectTokenAsync(new TokenIntrospectionRequest
        {
            Address = disco.IntrospectionEndpoint,

            ClientId = apiName,
            ClientSecret = apiSecret,

            Token = accessToken
        });

        if (result.IsError || !result.IsActive)
        {
            return false;
        }

        return true;
    }

    private void CreateCache(AuthorizationOptions authorizationOptions)
    {
        Options = authorizationOptions;
        
        if (string.IsNullOrWhiteSpace(Options.IssuerUri))
        {
            throw new ServiceConfigurationMissingException("Issuer URI is not configured.");
        }

        var url = new Uri(Options.IssuerUri);
        _cache = new DiscoveryCache(url.AbsoluteUri.TrimEnd('/'));
    }

    private static void ValidateResponse(ProtocolResponse response)
    {
        if (response.IsError)
        {
            throw new AuthorizationFailedException(response.Error, response.Error, response.Exception);
        }
    }

    protected async Task<DiscoveryDocumentResponse> GetDiscoveryResponse()
    {
        var disco = await Cache.GetAsync();
        ValidateResponse(disco);

        return disco;
    }
}
