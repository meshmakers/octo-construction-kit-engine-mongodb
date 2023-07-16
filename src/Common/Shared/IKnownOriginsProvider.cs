using System.Collections.Generic;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.Shared;

public interface IKnownOriginsProvider
{
    Task<IReadOnlyCollection<string>> GetKnownOriginsAsync();
}