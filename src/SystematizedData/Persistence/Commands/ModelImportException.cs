using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

[Serializable]
public class ModelImportException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public ModelImportException()
    {
    }

    public ModelImportException(string message) : base(message)
    {
    }

    public ModelImportException(string message, Exception inner) : base(message, inner)
    {
    }

    protected ModelImportException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
