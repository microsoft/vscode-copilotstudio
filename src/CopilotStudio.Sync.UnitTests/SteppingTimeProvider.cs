// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed class SteppingTimeProvider : TimeProvider
{
    private readonly object _gate = new();

    private readonly List<TimeSpan> _delays = new();

    private DateTimeOffset _now = new(2026, 10, 25, 19, 46, 0, TimeSpan.Zero);

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return _delays.ToArray();
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _delays.Add(dueTime);
            _now += dueTime;
        }

        ThreadPool.QueueUserWorkItem(_ => callback(state));

        return new ElapsedTimer();
    }

    private sealed class ElapsedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
