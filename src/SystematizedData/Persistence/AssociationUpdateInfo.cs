using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class AssociationUpdateInfo
{
    public AssociationUpdateInfo(RtEntityId origin, RtEntityId target, string roleId,
        AssociationModOptionsDto modOption)
    {
        Origin = origin;
        Target = target;
        RoleId = roleId;
        ModOption = modOption;
    }

    public RtEntityId Origin { get; }
    public RtEntityId Target { get; }

    public string RoleId { get; }
    public AssociationModOptionsDto ModOption { get; }
}
