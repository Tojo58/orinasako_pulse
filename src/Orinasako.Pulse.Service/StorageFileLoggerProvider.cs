using Microsoft.Extensions.Logging;

namespace Orinasako.Pulse.Service;

internal sealed class StorageFileLoggerProvider : ILoggerProvider
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StorageAgent", "logs", "agent.log");
    private readonly object _gate = new();
    private bool _disposed;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() => _disposed = true;

    public IReadOnlyList<string> ReadLast(int requestedLines)
    {
        var count = Math.Clamp(requestedLines, 1, 200);
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            return File.ReadLines(_path).TakeLast(count).Select(static line => line.Length <= 4000 ? line : line[..4000]).ToArray();
        }
    }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(_path) && new FileInfo(_path).Length > MaximumLogBytes)
            {
                var previous = _path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(_path, previous);
            }
            var safeException = exception is null ? string.Empty :
                $" | exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}";
            var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category} event={eventId.Id} {message}{safeException}{Environment.NewLine}";
            File.AppendAllText(_path, line);
        }
    }

    private sealed class FileLogger(StorageFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(category, logLevel, eventId, formatter(state, null), exception);
        }
    }
}
