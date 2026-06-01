using System.Text;
using AiTextEditor.Core.Infrastructure;

namespace AiTextEditor.Agent.CharacterBible.Diagnostics;

internal sealed class CharacterBibleRunLogger : ICharacterBibleRunLogger, IDisposable
{
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object syncRoot = new();
    private StreamWriter? writer;
    private bool disposed;

    private CharacterBibleRunLogger(CharacterBibleRunLogContext context)
    {
        Context = context;
        var directory = Path.GetDirectoryName(context.LogPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Character Bible log path '{context.LogPath}' has no directory.");
        }

        Directory.CreateDirectory(directory);
        writer = new StreamWriter(new FileStream(
            context.LogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite),
            LogEncoding)
        {
            AutoFlush = true
        };
    }

    public CharacterBibleRunLogContext Context { get; }

    public static CharacterBibleRunLogger Create(DateTimeOffset now)
    {
        var runId = now.ToString("yyyyMMdd-HHmmss");
        return new CharacterBibleRunLogger(new CharacterBibleRunLogContext(
            runId,
            AppLogPaths.CreateCharacterBibleRunLogPath(now),
            now));
    }

    public void Info(string eventName, string message)
    {
        Write("INF", eventName, message, null);
    }

    public void Debug(string eventName, string message)
    {
        Write("DBG", eventName, message, null);
    }

    public void DebugBlock(string eventName, string header, string block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(block);
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (syncRoot)
        {
            writer?.WriteLine(BuildLine("DBG", eventName.Trim() + ".begin", header));
            writer?.WriteLine(block);
            writer?.WriteLine(BuildLine("DBG", eventName.Trim() + ".end", header));
        }
    }

    public void Warning(string eventName, string message)
    {
        Write("WRN", eventName, message, null);
    }

    public void Error(string eventName, string message, Exception? exception = null)
    {
        Write("ERR", eventName, message, exception);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            writer?.Flush();
            writer?.Dispose();
            writer = null;
            disposed = true;
        }
    }

    private void Write(string level, string eventName, string message, Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ObjectDisposedException.ThrowIf(disposed, this);

        var line = BuildLine(level, eventName.Trim(), message);
        lock (syncRoot)
        {
            writer?.WriteLine(line);
            if (exception is not null)
            {
                writer?.WriteLine(LogValueFormatter.ShortText(exception.ToString(), 2000));
            }
        }
    }

    private static string BuildLine(string level, string eventName, string message)
        => $"{DateTimeOffset.Now:HH:mm:ss} {level} {eventName} {message}".TrimEnd();
}
