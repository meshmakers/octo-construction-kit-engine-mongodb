using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine;

[Serializable]
public class CkModelViolationException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public CkModelViolationException()
    {
    }

    public CkModelViolationException(string message) : base(message)
    {
    }

    public CkModelViolationException(string message, Exception inner) : base(message, inner)
    {
    }

    protected CkModelViolationException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
