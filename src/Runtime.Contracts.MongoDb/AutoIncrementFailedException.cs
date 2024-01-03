using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class AutoIncrementFailedException : OperationFailedException
{
    private AutoIncrementFailedException()
    {
    }

    private AutoIncrementFailedException(string message) : base(message)
    {
    }

    private AutoIncrementFailedException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception InvalidNullCurrentValue(OctoObjectId autoIncrementRtId)
    {
        return new AutoIncrementFailedException(
            $"Invalid null current value for autoincrement attribute '{autoIncrementRtId}'");
    }

    public static Exception AutoIncrementEndReached(OctoObjectId autoIncrementRtId)
    {
        return new AutoIncrementFailedException(
            $"Autoincrement end reached for attribute '{autoIncrementRtId}'");
    }
}