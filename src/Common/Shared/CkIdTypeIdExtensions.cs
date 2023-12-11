using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Common.Shared;

public static class CkIdTypeIdExtensions
{
    /// <summary>
    ///     Creates a GraphQL name of the given string
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    public static string GetGraphQlName(this CkId<CkTypeId> s)
    {
        return s.ToString().Replace(".", "");
    }
}