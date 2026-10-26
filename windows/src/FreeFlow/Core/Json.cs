using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeFlow.Core;

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Serializer = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Strict, like JSONSerialization on macOS: provider responses are untrusted
    // input, and malformed JSON must fall through to the plain-text path, not parse.
    public static readonly JsonDocumentOptions DocumentOptions = new();
}
