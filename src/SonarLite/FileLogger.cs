using System.IO;
using Microsoft.Extensions.Logging;

namespace SonarLite;

/// <summary>Minimal append-only file logger; the app has no console.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose() => _writer.Dispose();

    private void Write(string line)
    {
        lock (_writer)
            _writer.WriteLine(line);
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTime.Now:HH:mm:ss} {logLevel,-11} {category}: {formatter(state, exception)}";
            if (exception != null)
                line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
