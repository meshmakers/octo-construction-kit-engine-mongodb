using System.Runtime.Serialization;

namespace Meshmakers.Octo.Common.Shared.Services;

[Serializable]
public class NotificationSendFailedException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public NotificationSendFailedException()
    {
    }

    public NotificationSendFailedException(string message) : base(message)
    {
    }

    public NotificationSendFailedException(string message, Exception inner) : base(message, inner)
    {
    }

    protected NotificationSendFailedException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}