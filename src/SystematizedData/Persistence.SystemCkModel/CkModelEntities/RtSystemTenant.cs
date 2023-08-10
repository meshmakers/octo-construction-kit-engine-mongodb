using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

/// <summary>
/// Represents a tenant in the system.
/// </summary>
[CkId(SystemCkModel.SystemCkModelId, SystemCkModel.SystemTenantTypeId)]
public class RtSystemTenant : RtEntity
{
    /// <summary>
    /// Gets or sets the name of the tenant.
    /// </summary>
    public string TenantId
    {
        get => GetAttributeStringValue(nameof(TenantId));
        set => SetAttributeValue(nameof(TenantId), AttributeValueTypes.String, value);
    }
    
    /// <summary>
    /// Gets or sets the name of the database associated with this tenant.
    /// </summary>
    public string DatabaseName
    {
        get => GetAttributeStringValue(nameof(DatabaseName));
        set => SetAttributeValue(nameof(DatabaseName), AttributeValueTypes.String, value);
    }
}