using JsonFx.Json;
#if !NET472
using Newtonsoft.Json;
#endif

namespace Aetherfront.Core
{
    public static class AetherfrontJson
    {
        public static string Serialize<T>(T value)
        {
            return new JsonFx.Json.JsonWriter().Write(value);
        }

        public static T Deserialize<T>(string json)
        {
#if NET472
            return new JsonReader().Read<T>((json ?? string.Empty).TrimStart('\uFEFF'));
#else
            return JsonConvert.DeserializeObject<T>((json ?? string.Empty).TrimStart('\uFEFF'));
#endif
        }
    }
}
