using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.Common.Shared.Authorization;

[Serializable]
public class AuthorizationFailedException : Exception
{
    public AuthorizationFailedException(string error)
    {
        Error = error;
    }

    public AuthorizationFailedException(string error, string message) : base(message)
    {
        Error = error;
    }

    public AuthorizationFailedException(string error, string message, Exception inner) : base(message, inner)
    {
        Error = error;
    }

    public string Error { get; }
}
