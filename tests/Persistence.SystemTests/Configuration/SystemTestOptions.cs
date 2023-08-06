// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Meshmakers.Octo.Backend.Persistence.SystemTests.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
public class SystemTestOptions
{
    public string TenantId { get; set; } = null!;
    public string AdminUserPassword { get; set; } = null!;
    public string DatabaseUserPassword { get; set; } = null!;
}
