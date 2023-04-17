using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.Common.Shared;

[Serializable]
public class ServiceConfigurationMissingException : Exception
{
    public ServiceConfigurationMissingException()
    {
    }

    public ServiceConfigurationMissingException(string message) : base(message)
    {
    }

    public ServiceConfigurationMissingException(string message, Exception inner) : base(message, inner)
    {
    }

    protected ServiceConfigurationMissingException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
