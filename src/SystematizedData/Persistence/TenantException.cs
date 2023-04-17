using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.Backend.Persistence;

[Serializable]
public class TenantException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public TenantException()
    {
    }

    public TenantException(string message) : base(message)
    {
    }

    public TenantException(string message, Exception inner) : base(message, inner)
    {
    }

    protected TenantException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
