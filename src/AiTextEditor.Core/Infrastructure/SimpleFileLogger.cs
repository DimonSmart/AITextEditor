using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;

namespace AiTextEditor.Core.Infrastructure;

public class SimpleFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _filePath;
    private static readonly object _lock = new object();
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public SimpleFileLogger(string categoryName, string filePath)
    {
        _categoryName = categoryName;
        _filePath = filePath;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var logRecord = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_categoryName}] {message}";
        if (exception != null)
        {
            logRecord += Environment.NewLine + exception.ToString();
        }

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_filePath, logRecord + Environment.NewLine, LogEncoding);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }
}

public class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;

    public SimpleFileLoggerProvider(string filePath)
    {
        _filePath = filePath;
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
        return new SimpleFileLogger(categoryName, _filePath);
    }

    public void Dispose() { }
}
