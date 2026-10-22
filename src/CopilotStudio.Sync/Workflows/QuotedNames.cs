// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal static class QuotedNames
{
    public static string Join(IReadOnlyList<string> names)
    {
        var quoted = names.Select(name => $"'{name}'").ToArray();

        return quoted.Length < 2 ? string.Concat(quoted) : $"{string.Join(", ", quoted, 0, quoted.Length - 1)} and {quoted[quoted.Length - 1]}";
    }
}