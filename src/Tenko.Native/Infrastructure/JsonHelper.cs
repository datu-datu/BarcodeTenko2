using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tenko.Native.Infrastructure
{
    public static class JsonHelper
    {
        public static readonly JsonSerializerOptions DefaultOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        public static readonly JsonSerializerOptions IndentedOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, DefaultOptions);
        public static string Serialize<T>(T value, bool indent = false) => JsonSerializer.Serialize(value, indent ? IndentedOptions : DefaultOptions);
    }
}
