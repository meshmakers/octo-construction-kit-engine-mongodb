using System;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared;

public class NewtonEnumValueConverter : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue((int)value);
    }

    // ReSharper disable once RedundantOverriddenMember
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer)
    {
        return Convert.ChangeType(existingValue, objectType);
    }

    public override bool CanConvert(Type objectType)
    {
        return typeof(Enum).IsAssignableFrom(objectType);
    }
}
