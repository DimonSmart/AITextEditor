using AiTextEditor.Lib.Model.Intent;

namespace AiTextEditor.Lib.Interfaces;

public interface IIntentParser
{
    Task<IntentParseResult> ParseAsync(string userCommand, CancellationToken ct = default);
}
