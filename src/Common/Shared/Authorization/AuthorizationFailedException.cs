namespace Meshmakers.Octo.Common.Shared.Authorization;

[Serializable]
public class AuthorizationFailedException : Exception
{
    public AuthorizationFailedException(string message) : base(message)
    {
    }

    public AuthorizationFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }


    internal static Exception AuthenticationFailed(string? responseError, Exception? responseException)
    {
        return new AuthorizationFailedException(
            $"Authentication failed. Response error: {responseError}", responseException);
    }
}