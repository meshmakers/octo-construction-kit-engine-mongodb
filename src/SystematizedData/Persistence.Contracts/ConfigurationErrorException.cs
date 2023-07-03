using System;
using System.Runtime.Serialization;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

[Serializable]
public class ConfigurationErrorException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public ConfigurationErrorException()
    {
    }

    public ConfigurationErrorException(string message) : base(message)
    {
    }

    public ConfigurationErrorException(string message, Exception inner) : base(message, inner)
    {
    }

    protected ConfigurationErrorException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }

    public static Exception InvalidConfigurationValue(string configurationName, string? value)
    {
        return new ConfigurationErrorException(
            $"Impossible to apply '{configurationName}' setting with value '{value}': Value is absent, empty or contains whitespaces.");
    }
}
