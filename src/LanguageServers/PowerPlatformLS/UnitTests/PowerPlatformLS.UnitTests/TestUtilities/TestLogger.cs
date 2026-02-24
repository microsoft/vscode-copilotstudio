namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.Extensions.Logging;
    using Moq;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    internal class TestLogger : ILogger
    {
        public IEnumerable<string> Info => _logs.TryGetValue(LogLevel.Information, out var logs) ? logs : [];
        public IEnumerable<string> Warning => _logs.TryGetValue(LogLevel.Warning, out var logs) ? logs : [];
        public IEnumerable<string> Error => _logs.TryGetValue(LogLevel.Error, out var logs) ? logs : [];
        public IEnumerable<string> Critical => _logs.TryGetValue(LogLevel.Critical, out var logs) ? logs : [];
        public IEnumerable<string> Trace => _logs.TryGetValue(LogLevel.Trace, out var logs) ? logs : [];

        private readonly ConcurrentDictionary<LogLevel, ConcurrentQueue<string>> _logs = new();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_logs.TryGetValue(logLevel, out var logs))
            {
                logs = _logs[logLevel] = new ConcurrentQueue<string>();
            }

            var message = formatter(state, exception);
            logs.Enqueue(exception != null ? $"{exception}\n{message}" : message);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Mock.Of<IDisposable>();

        internal void Clear()
        {
            _logs.Clear();
        }
    }

    internal class TestLogger<T> : ILogger<T>
    {
        private readonly TestLogger _testLogger;

        public TestLogger(TestLogger testLogger)
        {
            _testLogger = testLogger;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Mock.Of<IDisposable>();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _testLogger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
