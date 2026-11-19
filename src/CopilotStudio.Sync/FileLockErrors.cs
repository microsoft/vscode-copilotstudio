// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal static class FileLockErrors
{
    private const int AccessDenied = 5;

    private const int SharingViolation = 32;

    private const int LockViolation = 33;

    internal static bool IsSharingViolation(IOException exception) => Win32ErrorOf(exception) is SharingViolation or LockViolation;

    internal static bool MayBeHeldByAnotherProcess(IOException exception) => Win32ErrorOf(exception) is AccessDenied or SharingViolation or LockViolation;

    private static int Win32ErrorOf(IOException exception) => exception.HResult & 0xFFFF;
}
