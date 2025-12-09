using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests.Infrastructure;

internal static class TestLoggerFactory
{
    public static ILoggerFactory Create(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new TestOutputLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    private sealed class TestOutputLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper output;

        public TestOutputLoggerProvider(ITestOutputHelper output)
        {
            this.output = output;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestOutputLogger(output, categoryName);
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestOutputLogger : ILogger
    {
        private readonly ITestOutputHelper output;
        private readonly string categoryName;
        private readonly object sync = new();

        public TestOutputLogger(ITestOutputHelper output, string categoryName)
        {
            this.output = output;
            this.categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            lock (sync)
            {
                output.WriteLine($"[{logLevel}] {categoryName}: {message}");
                if (exception != null)
                {
                    output.WriteLine(exception.ToString());
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
