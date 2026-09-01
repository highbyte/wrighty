using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Highbyte.Wrighty.UnitTests.Diagnostics;

internal sealed record RecordedLog(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties);

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLog> entries = new();
    private readonly TaskCompletionSource<RecordedLog> firstEntry = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<RecordedLog> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName) =>
        new RecordingLogger(categoryName, Record);

    public Task<RecordedLog> WaitForEntryAsync(CancellationToken cancellationToken) =>
        firstEntry.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
    }

    private void Record(RecordedLog entry)
    {
        entries.Enqueue(entry);
        firstEntry.TrySetResult(entry);
    }

    private sealed class RecordingLogger(
        string category,
        Action<RecordedLog> record) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            record(new RecordedLog(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                properties));
        }
    }
}
