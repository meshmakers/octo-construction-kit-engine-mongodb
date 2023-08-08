using System.Runtime.CompilerServices;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

[assembly:InternalsVisibleTo("CkModel.Compiler.Tests")]

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

internal static class CompilerStatics
{
    public static IEnumerable<CkId<CkTypeId>> WhiteListedCkIds { get; set; } =
        new CkId<CkTypeId>[] { new("System/Entity") };
}