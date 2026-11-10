// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal static class TimeProviderDelay
{
    public static Task WaitAsync(TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        return timeProvider.Delay(delay, cancellationToken);
#else
        return Task.Delay(delay, timeProvider, cancellationToken);
#endif
    }
}