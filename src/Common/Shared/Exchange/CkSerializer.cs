using System.IO;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public static class CkSerializer
{
    public static void Serialize(StreamWriter streamWriter, CkModelRoot model)
    {
        var serializer = new JsonSerializer { NullValueHandling = NullValueHandling.Ignore };

        using (JsonWriter writer = new JsonTextWriter(streamWriter))
        {
            serializer.Serialize(writer, model);
        }
    }

    public static CkModelRoot? Deserialize(string s)
    {
        return Deserialize(new StringReader(s));
    }

    public static CkModelRoot? Deserialize(TextReader textReader)
    {
        var serializer = new JsonSerializer { NullValueHandling = NullValueHandling.Ignore };

        using (JsonReader reader = new JsonTextReader(textReader))
        {
            return serializer.Deserialize<CkModelRoot>(reader);
        }
    }
}
