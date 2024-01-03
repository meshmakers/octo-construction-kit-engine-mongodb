namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class ConfigurationErrorException : Exception
{
    private ConfigurationErrorException()
    {
    }

    private ConfigurationErrorException(string message) : base(message)
    {
    }

    private ConfigurationErrorException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception InvalidConfigurationValue(string configurationName, string? value)
    {
        return new ConfigurationErrorException(
            $"Impossible to apply '{configurationName}' setting with value '{value}': Value is absent, empty or contains whitespaces.");
    }
}