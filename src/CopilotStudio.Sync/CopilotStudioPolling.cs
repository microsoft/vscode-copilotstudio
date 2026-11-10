// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal static class CopilotStudioPolling
{
    public static readonly PollingCadence Backoff = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    public static readonly PollingCadence EverySecond = new(TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(1));

    public static async Task<bool> UntilAsync(TimeProvider timeProvider, PollingCadence cadence, TimeSpan timeout, Func<CancellationToken, Task<bool>> isDone, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        var delay = cadence.FirstDelay;

        for (var remaining = timeout; remaining > TimeSpan.Zero; remaining = timeout - timeProvider.GetElapsedTime(startedAt))
        {
            await TimeProviderDelay.WaitAsync(timeProvider, remaining < delay ? remaining : delay, cancellationToken).ConfigureAwait(false);

            if (await isDone(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            delay = cadence.NextDelay(delay);
        }

        return false;
    }
}

internal sealed record PollingCadence(TimeSpan FirstDelay, TimeSpan DelayIncrease, TimeSpan MaximumDelay)
{
    public TimeSpan NextDelay(TimeSpan previousDelay) => previousDelay + DelayIncrease < MaximumDelay ? previousDelay + DelayIncrease : MaximumDelay;
}
