using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;

public interface IAutoIncrementModifier
{
    Task RunAutoIncrementAsync(IOctoSession session, string ckId, IEnumerable<RtEntity> rtEntities);
}