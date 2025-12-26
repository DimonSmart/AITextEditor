using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiTextEditor.Lib.Common
{
    public static class SerializationOptions
    {
        public static JsonSerializerOptions RelaxedCompact { get; } = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }
}
