// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

internal static class AsciiCharacters
{
    public static bool IsLetterOrDigit(char character) => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
}
