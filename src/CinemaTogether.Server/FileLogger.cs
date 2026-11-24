namespace CinemaTogether.Server;

public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string logDirectory = directory;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                lock (owner.gate)
                {
                    Directory.CreateDirectory(owner.logDirectory);
                    var path = Path.Combine(owner.logDirectory, $"cinema-{DateTime.UtcNow:yyyy-MM-dd}.log");
                    File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)}{Environment.NewLine}{exception}{Environment.NewLine}");
                    foreach (var file in new DirectoryInfo(owner.logDirectory).GetFiles("cinema-*.log").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))) file.Delete();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
