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
    ///     AB#9753: a batch needs more numbers than the range has left before its <c>End</c>. The block is refused as a
    ///     whole: no number of it is taken from the range. The numbers left count from the <c>StartValue</c> floor
    ///     (AB#9755) when <paramref name="startValue" /> is given.
    /// </summary>
    public static Exception AutoIncrementEndReached(OctoObjectId autoIncrementRtId, string rangeName,
        long currentValue, long end, int requestedCount, long? startValue = null)
    {
        var left = NumbersLeft(currentValue, end, startValue);
        return new AutoIncrementFailedException(
            $"Autoincrement end reached for number range '{rangeName}' (counter '{autoIncrementRtId}'): the batch needs " +
            $"{requestedCount} number(s), {left} left up to End {end} (current value {currentValue}). " +
            "The block is refused; no number of it was taken from the range.");
    }

    private static long NumbersLeft(long currentValue, long end, long? startValue)
    {
        var floor = startValue is > 1 ? Math.Max(currentValue, startValue.Value - 1) : currentValue;
        return Math.Max(0, end - floor);
    }

    /// <summary>
    ///     AB#9754: the batch needs more numbers than the fiscal year <paramref name="fiscalYear" /> of a fiscal-year
    ///     range has left before its <c>End</c> (which bounds every fiscal year).
    /// </summary>
    public static Exception AutoIncrementEndReached(OctoObjectId autoIncrementRtId, string rangeName, int fiscalYear,
        long currentValue, long end, int requestedCount, long? startValue = null)
    {
        var left = NumbersLeft(currentValue, end, startValue);
        return new AutoIncrementFailedException(
            $"Autoincrement end reached for number range '{rangeName}' (counter '{autoIncrementRtId}'), fiscal year " +
            $"{fiscalYear}: the batch needs {requestedCount} number(s), {left} left up to End {end} (current value " +
            $"{currentValue}). The block is refused; no number of it was taken from this fiscal year.");
    }

    /// <summary>
    ///     AB#9754: the configuration of a number range is not valid (e.g. <c>PeriodKind = FiscalYear</c> without a
    ///     known IANA <c>ReferenceTimeZone</c>, or a token format that does not parse). Raised when the range is
    ///     configured and, for a range that got there by another path, before any number is allocated from it.
    /// </summary>
    public static Exception InvalidNumberRange(OctoObjectId autoIncrementRtId, string? rangeName, string reason)
    {
        var name = string.IsNullOrEmpty(rangeName) ? string.Empty : $" '{rangeName}'";
        return new AutoIncrementFailedException(
            $"Number range{name} (counter '{autoIncrementRtId}') is not valid: {reason}");
    }
}