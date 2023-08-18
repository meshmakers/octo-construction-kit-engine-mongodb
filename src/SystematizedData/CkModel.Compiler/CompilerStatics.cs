using System.Runtime.CompilerServices;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

[assembly:InternalsVisibleTo("CkModel.Compiler.Tests")]
[assembly:InternalsVisibleTo("CkModel.Compiler.SystemTests")]

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

internal static class CompilerStatics
{
    public static IEnumerable<CkId<CkTypeId>> WhiteListedCkTypeIds { get; } =
        new CkId<CkTypeId>[] { new("System/Entity") };
    
    public const string AttributesDirectoryName = "attributes";
    public const string AssociationsDirectoryName = "associations";
    public const string TypesDirectoryName = "types";
    public const string MetadataFile = "ckModel.yaml";
}