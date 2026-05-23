using System.Text.RegularExpressions;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Web.Services;

public sealed partial class CharacterBibleCommandParser
{
    public CharacterBibleCommandParseResult Parse(string userCommand)
    {
        if (string.IsNullOrWhiteSpace(userCommand))
        {
            return CharacterBibleCommandParseResult.Rejected("Enter an operation command.");
        }

        var normalized = userCommand.Trim();
        if (IsGenerateCommand(normalized) || IsRefreshAllCommand(normalized))
        {
            return CharacterBibleCommandParseResult.Parsed(new CharacterBibleOperationRequest(normalized, null));
        }

        if (IsRefreshCommand(normalized))
        {
            var pointers = ExtractPointers(normalized);
            if (pointers.Count == 0)
            {
                return CharacterBibleCommandParseResult.Rejected("Refresh command must include at least one valid semantic pointer.");
            }

            return CharacterBibleCommandParseResult.Parsed(new CharacterBibleOperationRequest(normalized, pointers));
        }

        return CharacterBibleCommandParseResult.Rejected("Unsupported command. Use generate, refresh all, or refresh with explicit pointers.");
    }

    private static bool IsGenerateCommand(string command)
    {
        return Contains(command, "generate character bible")
            || Contains(command, "create character bible")
            || Contains(command, "создай каталог досье персонажей книги")
            || Contains(command, "создай досье персонажей книги")
            || Contains(command, "составь библию персонажей");
    }

    private static bool IsRefreshAllCommand(string command)
    {
        return Contains(command, "refresh all")
            || Contains(command, "refresh character bible all")
            || Contains(command, "обнови всю библию")
            || Contains(command, "обнови библию целиком");
    }

    private static bool IsRefreshCommand(string command)
    {
        return Contains(command, "refresh character bible")
            || Contains(command, "refresh bible")
            || Contains(command, "обнови библию");
    }

    private static IReadOnlyCollection<string> ExtractPointers(string command)
    {
        var pointers = new List<string>();
        foreach (Match match in PointerTokenRegex().Matches(command))
        {
            var token = match.Value.Trim(' ', '.', ',', ';', ':', ')', ']', '}');
            if (SemanticPointer.TryParse(token, out var pointer) && pointer is not null)
            {
                pointers.Add(pointer.ToCompactString());
            }
        }

        return pointers
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Contains(string command, string expected)
    {
        return command.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:\d+(?:\.\d+)*\.?p\d+|p\d+)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase)]
    private static partial Regex PointerTokenRegex();
}
