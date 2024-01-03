using System.Runtime.Serialization;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class AutoIncrementFailedException : OperationFailedException
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public AutoIncrementFailedException()
    {
    }

    public AutoIncrementFailedException(string message) : base(message)
    {
    }

    public AutoIncrementFailedException(string message, Exception inner) : base(message, inner)
    {
    }
}