using System.Runtime.Serialization;

namespace Persistence.Commands;

[Serializable]
public class ModelExportException : Exception
{

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
