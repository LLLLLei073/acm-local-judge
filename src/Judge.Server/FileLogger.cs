namespace Judge.Server;

// Minimal file logger with daily rotation: data/logs/judge-YYYYMMDD.log. Framework noise is capped
// at Warning so the file stays readable; application categories log from Information upward.
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object gate = new();
    private StreamWriter? writer;
    private string date = "";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    private void Write(string message)
    {
        lock (gate)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyyMMdd");
                if (writer == null || today != date)
                {
                    writer?.Dispose();
                    Directory.CreateDirectory(directory);
                    date = today;
                    writer = new StreamWriter(Path.Combine(directory, $"judge-{today}.log"), append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
                }
                writer.WriteLine(message);
            }
            catch { /* Logging must never take the judge down. */ }
        }
    }
    public void Dispose() { lock (gate) { try { writer?.Dispose(); } catch { } writer = null; } }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= (category.StartsWith("Microsoft") ? LogLevel.Warning : LogLevel.Information);
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || exception != null && exception is OperationCanceledException) return;
            owner.Write($"{DateTime.UtcNow:HH:mm:ss.fff} {level} [{category}] {formatter(state, exception)}{(exception == null ? "" : "\n" + exception)}");
        }
    }
}
