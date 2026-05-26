using System.Text.Json;
using DimonSmart.AiUtils;

namespace AiTextEditor.Agent;

internal static class StructuredJsonResponseRecovery
{
    public static bool TryRecover<TResponse>(
        string? rawText,
        JsonSerializerOptions serializerOptions,
        out TResponse? response,
        out string? extractedJson,
        out string? error)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);

        response = null;
        extractedJson = null;

        if (string.IsNullOrWhiteSpace(rawText))
        {
            error = "Raw response text is empty.";
            return false;
        }

        if (TryDeserialize(rawText, serializerOptions, out response, out error))
        {
            extractedJson = rawText;
            return true;
        }

        var jsonFragment = JsonExtractor.ExtractJson(rawText);
        if (string.IsNullOrWhiteSpace(jsonFragment))
        {
            extractedJson = null;
            error = "No valid JSON fragment was found in the raw response.";
            return false;
        }

        extractedJson = jsonFragment;
        return TryDeserialize(extractedJson, serializerOptions, out response, out error);
    }

    private static bool TryDeserialize<TResponse>(
        string json,
        JsonSerializerOptions serializerOptions,
        out TResponse? response,
        out string? error)
        where TResponse : class
    {
        try
        {
            response = JsonSerializer.Deserialize<TResponse>(json, serializerOptions);
            if (response is null)
            {
                error = "JSON deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            response = null;
            error = ex.Message;
            return false;
        }
    }
}
