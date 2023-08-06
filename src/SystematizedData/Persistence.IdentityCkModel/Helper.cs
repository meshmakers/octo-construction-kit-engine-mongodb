
namespace Persistence.IdentityCkModel;

internal static class Helper
{
    internal static string AssemblyDirectory
    {
        get
        {
            var location = typeof(Helper).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(location);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                throw new InvalidOperationException($"Could not determine assembly directory for {location}");
            }

            return assemblyDirectory;
        }
    }
}
