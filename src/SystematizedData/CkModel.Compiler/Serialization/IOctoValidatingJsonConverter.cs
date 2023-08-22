using Json.Schema;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

internal interface IOctoValidatingJsonConverter
{
    OutputFormat OutputFormat { get; set; }

    bool RequireFormatValidation { get; set; }
}