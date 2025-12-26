using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class KeywordCursorCreationPlugin(IKeywordCursorRegistry cursorRegistry, ILogger<KeywordCursorCreationPlugin> logger)
{
    private readonly IKeywordCursorRegistry cursorRegistry = cursorRegistry;
    private readonly ILogger<KeywordCursorCreationPlugin> logger = logger;
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
    private static readonly string[] CyrillicSuffixes = new[]
    {
        "\u0441\u043a\u0438\u0439",
        "\u0446\u043a\u0438\u0439",
        "\u0438\u043d",
        "\u044b\u043d",
        "\u043e\u0432",
        "\u0435\u0432",
        "\u0451\u0432",
        "\u0438\u0439",
        "\u044b\u0439",
        "\u043e\u0439",
        "\u0430\u044f",
        "\u043e\u0435",
        "\u044b\u0435",
        "\u0438\u0435"
    };

    [KernelFunction("create_keyword_cursor")]
    [Description("Create a keyword cursor that yields matching document items in order.")]
    public string CreateKeywordCursor([Description("Keywords to locate in the document. Use word stems to ensure matching against inflected forms.")] string[] keywords)
    {
        var normalizedKeywords = NormalizeKeywords(keywords);
        var cursorName = cursorRegistry.CreateCursor(normalizedKeywords);
        logger.LogInformation("create_keyword_cursor: cursor={Cursor}", cursorName);
        return cursorName;
    }

    private static string[] NormalizeKeywords(string[] keywords)
    {
        if (keywords == null || keywords.Length == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = new List<string>(keywords.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            foreach (Match match in TokenRegex.Matches(keyword))
            {
                var token = match.Value.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                var stem = StemToken(token.ToLowerInvariant());
                if (stem.Length == 0)
                {
                    continue;
                }

                if (seen.Add(stem))
                {
                    normalized.Add(stem);
                }
            }
        }

        if (normalized.Count == 0)
        {
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                var trimmed = keyword.Trim().ToLowerInvariant();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (seen.Add(trimmed))
                {
                    normalized.Add(trimmed);
                }
            }
        }

        return normalized.ToArray();
    }

    private static string StemToken(string token)
    {
        if (token.Length < 3 || !ContainsCyrillic(token))
        {
            return token;
        }

        var stem = token;
        if (stem.Length > 5 && IsCyrillicVowelOrSoft(stem[^1]))
        {
            stem = stem[..^1];
        }

        if (stem.Length > 5)
        {
            foreach (var suffix in CyrillicSuffixes)
            {
                if (stem.EndsWith(suffix, StringComparison.Ordinal))
                {
                    stem = stem[..^suffix.Length];
                    break;
                }
            }
        }

        return stem.Length < 3 ? token : stem;
    }

    private static bool ContainsCyrillic(string token)
    {
        foreach (var ch in token)
        {
            if (ch >= '\u0400' && ch <= '\u04FF')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCyrillicVowelOrSoft(char value)
    {
        return value is '\u0430'
            or '\u044f'
            or '\u0435'
            or '\u0451'
            or '\u0438'
            or '\u043e'
            or '\u0443'
            or '\u044b'
            or '\u044d'
            or '\u044e'
            or '\u0439'
            or '\u044c';
    }
}
