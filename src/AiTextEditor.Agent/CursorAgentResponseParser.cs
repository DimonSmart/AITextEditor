using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiTextEditor.Core.Model;
using DimonSmart.AiUtils;

namespace AiTextEditor.Agent;

public interface ICursorAgentResponseParser
{
    AgentCommand? ParseCommand(string content);

    FinalizerResponse? ParseFinalizer(string content);
}

public sealed class CursorAgentResponseParser : ICursorAgentResponseParser
{
    public AgentCommand? ParseCommand(string content)
    {
        var commands = JsonExtractor
            .ExtractAllJsons(content)
            .Select(ParseSingle)
            .Where(command => command != null)
            .Cast<AgentCommand>()
            .ToList();

        if (commands.Count == 0)
        {
            return null;
        }

        var multipleActions = commands.Count > 1;
        var finish = commands.FirstOrDefault(command => command.Action == "stop");

        var selected = finish ?? commands[0];
        return selected.WithMultipleCandidates(multipleActions);
    }

    public FinalizerResponse? ParseFinalizer(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var decision = root.TryGetProperty("decision", out var decisionElement) && decisionElement.ValueKind == JsonValueKind.String
                ? decisionElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(decision))
            {
                return null;
            }

            var semanticPointerFrom = root.TryGetProperty("semanticPointerFrom", out var pointerElement) && pointerElement.ValueKind == JsonValueKind.String
                ? pointerElement.GetString()
                : null;

            var excerpt = root.TryGetProperty("excerpt", out var excerptElement) && excerptElement.ValueKind == JsonValueKind.String
                ? excerptElement.GetString()
                : null;

            var whyThis = root.TryGetProperty("whyThis", out var whyElement) && whyElement.ValueKind == JsonValueKind.String
                ? whyElement.GetString()
                : null;

            var markdown = root.TryGetProperty("markdown", out var markdownElement) && markdownElement.ValueKind == JsonValueKind.String
                ? markdownElement.GetString()
                : null;

            var summary = root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString()
                : null;

            return new FinalizerResponse(decision!, semanticPointerFrom, excerpt, whyThis, markdown, summary) { RawContent = content };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AgentCommand? ParseSingle(string content)
    {
        return TryParseJson(content);
    }

    private static AgentCommand? TryParseJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var action = root.TryGetProperty("action", out var actionElement) && actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : null;

            // Fallback for legacy/hallucinated "decision"
            if (string.IsNullOrWhiteSpace(action))
            {
                var decision = root.TryGetProperty("decision", out var decisionElement) && decisionElement.ValueKind == JsonValueKind.String
                    ? decisionElement.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(decision))
                {
                    action = decision switch
                    {
                        "done" => "stop",
                        _ => "continue"
                    };
                }
            }

            if (string.IsNullOrWhiteSpace(action))
            {
                return null;
            }

            var batchFound = false;
            if (root.TryGetProperty("batchFound", out var batchFoundElement))
            {
                batchFound = batchFoundElement.ValueKind == JsonValueKind.True;
            }

            var newEvidence = root.TryGetProperty("newEvidence", out var evidenceElement) && evidenceElement.ValueKind == JsonValueKind.Array
                ? ParseEvidenceArray(evidenceElement)
                : null;

            string? progress = null;
            if (root.TryGetProperty("progress", out var progressElement) && progressElement.ValueKind == JsonValueKind.String)
            {
                progress = progressElement.GetString();
            }

            var needMoreContext = root.TryGetProperty("needMoreContext", out var needElement) && needElement.ValueKind == JsonValueKind.True;

            return new AgentCommand(action!, batchFound, newEvidence, progress, needMoreContext) { RawContent = content };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<EvidenceItem> ParseEvidenceArray(JsonElement element)
    {
        var items = new List<EvidenceItem>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var evidence = ParseEvidence(item);
            if (evidence != null)
            {
                items.Add(evidence);
            }
        }

        return items;
    }

    private static EvidenceItem? ParseEvidence(JsonElement element)
    {
        string? pointer = null;
        if (element.TryGetProperty("pointer", out var pointerElement) && pointerElement.ValueKind == JsonValueKind.String)
        {
            pointer = pointerElement.GetString();
        }

        if (pointer == null)
        {
            return null;
        }

        var excerpt = element.TryGetProperty("excerpt", out var excerptElement) && excerptElement.ValueKind == JsonValueKind.String
            ? excerptElement.GetString()
            : null;

        if (excerpt == null && element.TryGetProperty("markdown", out var markdownElement) && markdownElement.ValueKind == JsonValueKind.String)
        {
            excerpt = markdownElement.GetString();
        }

        if (excerpt == null && element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            excerpt = textElement.GetString();
        }

        var reason = element.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString()
            : null;

        return new EvidenceItem(pointer!, excerpt, reason);
    }
}

public sealed record AgentCommand(string Action, bool BatchFound, IReadOnlyList<EvidenceItem>? NewEvidence, string? Progress, bool NeedMoreContext)
{
    public string? RawContent { get; init; }
    public bool MultipleJsonCandidates { get; init; }

    public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    public AgentCommand WithMultipleCandidates(bool multiple) => this with { MultipleJsonCandidates = multiple };
}

public sealed record FinalizerResponse(string Decision, string? SemanticPointerFrom, string? Excerpt, string? WhyThis, string? Markdown, string? Summary)
{
    public string? RawContent { get; init; }

    public FinalizerResponse WithRawContent(string raw) => this with { RawContent = raw };
}
