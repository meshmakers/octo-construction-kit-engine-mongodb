using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.SystematizedData.Persistence;

[Serializable]
public class DatabaseException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public DatabaseException()
    {
    }

    public DatabaseException(string message) : base(message)
    {
    }

    public DatabaseException(string message, Exception inner) : base(message, inner)
    {
    }

    protected DatabaseException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
