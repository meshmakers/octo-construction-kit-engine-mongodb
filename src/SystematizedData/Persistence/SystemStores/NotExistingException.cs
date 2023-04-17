using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

[Serializable]
public class NotExistingException : Exception
{
    public NotExistingException()
    {
    }

    public NotExistingException(string message) : base(message)
    {
    }

    public NotExistingException(string message, Exception inner) : base(message, inner)
    {
    }

    protected NotExistingException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
