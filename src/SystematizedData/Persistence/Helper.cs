using System.IO;

namespace Meshmakers.Octo.SystematizedData.Persistence;

internal static class Helper
{
    internal static string AssemblyDirectory
    {
        get
        {
            var location = typeof(Helper).Assembly.Location;
            return Path.GetDirectoryName(location);
        }
    }
}
