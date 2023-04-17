using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.Common.Shared.Exchange;

[Serializable]
public class ModelSerializerException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public ModelSerializerException()
    {
    }

    public ModelSerializerException(string message) : base(message)
    {
    }

    public ModelSerializerException(string message, Exception inner) : base(message, inner)
    {
    }

    protected ModelSerializerException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
