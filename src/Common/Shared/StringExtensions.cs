namespace Meshmakers.Octo.Common.Shared;

public static class StringExtensions
{
    public static string MakeKey(this string s)
    {
        return s.Trim().ToLower();
    }

    /// <summary>
    ///     Creates a GraphQL name of the given string
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    public static string GetGraphQlName(this string s)
    {
        return s.Replace(".", "");
    }
}
