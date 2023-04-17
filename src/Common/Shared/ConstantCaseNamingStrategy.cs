using Meshmakers.Common.Shared;
using Newtonsoft.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared;

public class ConstantCaseNamingStrategy : NamingStrategy
{
    protected override string ResolvePropertyName(string name)
    {
        return name.ToConstantCase();
    }
}
