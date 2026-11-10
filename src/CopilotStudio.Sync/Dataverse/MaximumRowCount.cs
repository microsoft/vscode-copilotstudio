// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

internal static class MaximumRowCount
{
    public static void Require(int? maximumCount)
    {
        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), maximumCount, "A row cap cannot be negative.");
        }
    }
}
