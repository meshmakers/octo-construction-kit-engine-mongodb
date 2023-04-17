using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.Backend.Persistence.Commands;

[Serializable]
public class ModelExportException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public ModelExportException()
    {
    }

    public ModelExportException(string message) : base(message)
    {
    }

    public ModelExportException(string message, Exception inner) : base(message, inner)
    {
    }

    protected ModelExportException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
