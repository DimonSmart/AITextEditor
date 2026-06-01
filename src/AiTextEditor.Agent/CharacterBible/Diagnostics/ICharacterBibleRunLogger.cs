namespace AiTextEditor.Agent.CharacterBible.Diagnostics;

internal interface ICharacterBibleRunLogger
{
    CharacterBibleRunLogContext Context { get; }

    void Info(string eventName, string message);

    void Debug(string eventName, string message);

    void DebugBlock(string eventName, string header, string block);

    void Warning(string eventName, string message);

    void Error(string eventName, string message, Exception? exception = null);
}
