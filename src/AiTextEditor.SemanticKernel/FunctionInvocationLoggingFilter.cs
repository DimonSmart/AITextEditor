using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

/// <summary>
/// Logs kernel function calls (both manual and auto-invoked) with arguments and results.
/// </summary>
public sealed class FunctionInvocationLoggingFilter : IFunctionInvocationFilter, IAutoFunctionInvocationFilter
{
    private const int MaxPayloadLength = 4000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<FunctionInvocationLoggingFilter> logger;

    public FunctionInvocationLoggingFilter(ILogger<FunctionInvocationLoggingFilter> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var pluginName = context.Function?.PluginName ?? "<unknown>";
        var functionName = context.Function?.Name ?? "<unknown>";
        var arguments = SerializeArguments(context.Arguments);

        logger.LogInformation(
            "function_invoke: plugin={Plugin}, function={Function}, streaming={Streaming}, args={Args}",
            pluginName,
            functionName,
            context.IsStreaming,
            arguments);

        var stopwatch = Stopwatch.StartNew();
        await next(context).ConfigureAwait(false);
        stopwatch.Stop();

        var result = SerializeResult(context.Result);
        logger.LogInformation(
            "function_result: plugin={Plugin}, function={Function}, durationMs={Duration:F2}, result={Result}",
            pluginName,
            functionName,
            stopwatch.Elapsed.TotalMilliseconds,
            result);
    }

    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var pluginName = context.Function?.PluginName ?? "<unknown>";
        var functionName = context.Function?.Name ?? "<unknown>";
        var arguments = SerializeArguments(context.Arguments);

        logger.LogInformation(
            "auto_invoke: callId={CallId}, plugin={Plugin}, function={Function}, requestIndex={RequestIndex}, functionIndex={FunctionIndex}, args={Args}",
            context.ToolCallId ?? "<none>",
            pluginName,
            functionName,
            context.RequestSequenceIndex,
            context.FunctionSequenceIndex,
            arguments);

        var stopwatch = Stopwatch.StartNew();
        await next(context).ConfigureAwait(false);
        stopwatch.Stop();

        var result = SerializeResult(context.Result);
        logger.LogInformation(
            "auto_result: callId={CallId}, plugin={Plugin}, function={Function}, durationMs={Duration:F2}, result={Result}",
            context.ToolCallId ?? "<none>",
            pluginName,
            functionName,
            stopwatch.Elapsed.TotalMilliseconds,
            result);
    }

    private static string SerializeArguments(object? arguments)
    {
        if (arguments is null)
        {
            return "(null)";
        }

        if (arguments is KernelArguments kernelArguments)
        {
            var dict = kernelArguments.ToDictionary(pair => pair.Key, pair => SimplifyValue(pair.Value));
            return SerializeDictionary(dict);
        }

        if (arguments is IEnumerable<KeyValuePair<string, object?>> enumerable)
        {
            var dict = enumerable.ToDictionary(pair => pair.Key, pair => SimplifyValue(pair.Value));
            return SerializeDictionary(dict);
        }

        return SerializeDictionary(new Dictionary<string, object?> { { "value", SimplifyValue(arguments) } });
    }

    private static string SerializeResult(FunctionResult? result)
    {
        if (result is null)
        {
            return "(null)";
        }

        var payload = new Dictionary<string, object?>
        {
            ["value"] = SimplifyValue(result.GetValue<object?>()),
            ["valueType"] = result.ValueType?.ToString()
        };

        if (result.Metadata?.Count > 0)
        {
            payload["metadata"] = result.Metadata.ToDictionary(pair => pair.Key, pair => SimplifyValue(pair.Value));
        }

        return SerializeDictionary(payload);
    }

    private static object? SimplifyValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            JsonElement json => ParseJsonElement(json),
            ValueType v => v,
            KernelArguments nestedArgs => nestedArgs.ToDictionary(pair => pair.Key, pair => SimplifyValue(pair.Value)),
            IEnumerable<KeyValuePair<string, object?>> kvps => kvps.ToDictionary(pair => pair.Key, pair => SimplifyValue(pair.Value)),
            _ => value.ToString()
        };
    }

    private static object? ParseJsonElement(JsonElement element)
    {
        try
        {
            var raw = element.GetRawText();
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            return JsonSerializer.Deserialize<object>(raw, SerializerOptions);
        }
        catch
        {
            return element.ToString();
        }
    }

    private static string SerializeDictionary(IDictionary<string, object?> data)
    {
        try
        {
            var serialized = JsonSerializer.Serialize(data, SerializerOptions);
            return Truncate(serialized);
        }
        catch (Exception ex)
        {
            return $"<serialization_error: {ex.Message}>";
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxPayloadLength)
        {
            return value;
        }

        return value[..MaxPayloadLength] + $"... (+{value.Length - MaxPayloadLength} chars)";
    }
}
