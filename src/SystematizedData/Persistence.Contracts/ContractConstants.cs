namespace Persistence.Contracts;

internal static class ContractConstants
{
    public const string RegexMongoDbHost = @"^([a-zA-Z0-9_.-]+)(:[0-9]{1,5})?$";
    public const string RegexWithoutWhitespaces = @"^[^\s]+$";
}