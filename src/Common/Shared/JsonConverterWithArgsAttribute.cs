using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Enum |
                AttributeTargets.Property | AttributeTargets.Field)]
public class JsonConverterWithArgsAttribute : JsonConverterAttribute
{
    public JsonConverterWithArgsAttribute(Type converterType, params object?[] converterArguments)
    {
        ConverterType = converterType;
        ConverterArguments = converterArguments;
    }

    public new Type ConverterType { get; }
    public object?[] ConverterArguments { get; }

    public override JsonConverter CreateConverter(Type _)
    {
        return (JsonConverter)Activator.CreateInstance(ConverterType, ConverterArguments)!;
    }
}