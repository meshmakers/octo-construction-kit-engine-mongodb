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

    /// <summary>
    ///     AB#9753: a batch needs more numbers than the range has left before its <c>End</c>. The batch is refused as a
    ///     whole; no number is allocated.
    /// </summary>
    public static Exception AutoIncrementEndReached(OctoObjectId autoIncrementRtId, string rangeName,
        long currentValue, long end, int requestedCount)
    {
        var left = Math.Max(0, end - currentValue);
        return new AutoIncrementFailedException(
            $"Autoincrement end reached for number range '{rangeName}' (counter '{autoIncrementRtId}'): the batch needs " +
            $"{requestedCount} number(s), {left} left up to End {end} (current value {currentValue}). " +
            "The batch is refused; no number was allocated.");
    }
}