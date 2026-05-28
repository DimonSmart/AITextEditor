using Microsoft.Extensions.Logging;
using System.Text;

namespace AiTextEditor.Core.Infrastructure;

public sealed class SimpleFileLogger : ILogger
{
    private static readonly object SyncRoot = new();
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string filePath;
    private readonly LogLevel minimumLevel;

    public SimpleFileLogger(string filePath, LogLevel minimumLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = filePath;
        this.minimumLevel = minimumLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= minimumLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var logRecord = $"{DateTimeOffset.Now:HH:mm:ss} {FormatLevel(logLevel)} {message}";
        if (exception != null)
        {
            logRecord += Environment.NewLine + exception.ToString();
        }

        lock (SyncRoot)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(filePath, logRecord + Environment.NewLine, LogEncoding);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }

    private static string FormatLevel(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "LOG"
        };
    }
}

public sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string filePath;
    private readonly LogLevel minimumLevel;

    public SimpleFileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Debug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = filePath;
        this.minimumLevel = minimumLevel;
    }

    public static string CreateTimestampedPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Log file path must be provided.", nameof(filePath));
        }

        var directory = Path.GetDirectoryName(filePath);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "log";
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{baseName}_{stamp}_{Environment.ProcessId}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? fileName
            : Path.Combine(directory, fileName);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new SimpleFileLogger(filePath, minimumLevel);
    }

    public void Dispose() { }
}
