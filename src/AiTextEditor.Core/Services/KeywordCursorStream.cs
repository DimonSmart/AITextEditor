using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AiTextEditor.Core.Model;
using Lucene.Net.Tartarus.Snowball;
using Lucene.Net.Tartarus.Snowball.Ext;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Core.Services;

public sealed class KeywordCursorStream : FilteredCursorStream
{
    private readonly IReadOnlyList<string> keywords;
    private readonly IReadOnlyList<KeywordEntry> keywordEntries;
    private static readonly Regex TokenRegex = new(@"\p{L}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ThreadLocal<EnglishStemmer> EnglishStemmer = new(() => new EnglishStemmer());
    private static readonly ThreadLocal<RussianStemmer> RussianStemmer = new(() => new RussianStemmer());

    public KeywordCursorStream(LinearDocument document, IEnumerable<string> keywords, int maxElements, int maxBytes, string? startAfterPointer, bool includeHeadings = true, ILogger? logger = null)
        : base(document, maxElements, maxBytes, startAfterPointer, includeHeadings, logger)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var normalized = keywords
            .Select(keyword => keyword?.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one keyword is required.", nameof(keywords));
        }

        this.keywords = normalized;
        keywordEntries = normalized.Select(BuildEntry).ToArray();
    }

    public override string? FilterDescription => $"Keywords: {string.Join(", ", keywords)}";

    protected override bool IsMatch(LinearItem item)
    {
        foreach (var entry in keywordEntries)
        {
            if (entry.Stems.Length == 0 && ContainsIgnoreCase(item.Text, entry.Original))
            {
                return true;
            }

            if (entry.Stems.Length == 0 && ContainsIgnoreCase(item.Markdown, entry.Original))
            {
                return true;
            }
        }

        if (keywordEntries.All(entry => entry.Stems.Length == 0))
        {
            return false;
        }

        var stems = BuildStemSet(item.Text);
        if (stems.Count == 0)
        {
            return false;
        }

        foreach (var entry in keywordEntries)
        {
            if (entry.Stems.Length == 0)
            {
                continue;
            }

            if (entry.Stems.All(stem => stems.Contains(stem)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string? text, string keyword)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static KeywordEntry BuildEntry(string keyword)
    {
        var stems = BuildKeywordStems(keyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new KeywordEntry(keyword, stems);
    }

    private static IEnumerable<string> BuildKeywordStems(string keyword)
    {
        foreach (var token in Tokenize(keyword))
        {
            var stem = StemToken(token);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                yield return stem;
            }
        }
    }

    private static HashSet<string> BuildStemSet(string text)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(text))
        {
            var stem = StemToken(token);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                stems.Add(stem);
            }
        }

        return stems;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in TokenRegex.Matches(text))
        {
            if (match.Success)
            {
                yield return match.Value;
            }
        }
    }

    private static string StemToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var script = DetectScript(token);
        var lower = token.ToLowerInvariant();

        return script switch
        {
            Script.Cyrillic => StemWith(RussianStemmer.Value!, lower),
            Script.Latin => StemWith(EnglishStemmer.Value!, lower),
            _ => lower
        };
    }

    private static string StemWith(SnowballProgram stemmer, string token)
    {
        stemmer.SetCurrent(token);
        stemmer.Stem();
        return stemmer.Current;
    }

    private static Script DetectScript(string token)
    {
        var hasCyrillic = false;
        var hasLatin = false;

        foreach (var ch in token)
        {
            if (IsCyrillic(ch))
            {
                hasCyrillic = true;
            }
            else if (IsLatin(ch))
            {
                hasLatin = true;
            }

            if (hasCyrillic && hasLatin)
            {
                return Script.Other;
            }
        }

        if (hasCyrillic)
        {
            return Script.Cyrillic;
        }

        if (hasLatin)
        {
            return Script.Latin;
        }

        return Script.Other;
    }

    private static bool IsCyrillic(char ch)
    {
        return (ch >= '\u0400' && ch <= '\u04FF')
            || (ch >= '\u0500' && ch <= '\u052F')
            || (ch >= '\u2DE0' && ch <= '\u2DFF')
            || (ch >= '\uA640' && ch <= '\uA69F');
    }

    private static bool IsLatin(char ch)
    {
        return (ch >= 'A' && ch <= 'Z')
            || (ch >= 'a' && ch <= 'z')
            || (ch >= '\u00C0' && ch <= '\u024F');
    }

    private sealed record KeywordEntry(string Original, string[] Stems);

    private enum Script
    {
        Cyrillic,
        Latin,
        Other
    }
}
