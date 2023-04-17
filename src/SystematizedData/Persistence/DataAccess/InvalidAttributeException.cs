using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

[Serializable]
public class InvalidAttributeException : OperationFailedException
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public InvalidAttributeException()
    {
    }

    public InvalidAttributeException(string message) : base(message)
    {
    }

    public InvalidAttributeException(string message, Exception inner) : base(message, inner)
    {
    }

    protected InvalidAttributeException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
