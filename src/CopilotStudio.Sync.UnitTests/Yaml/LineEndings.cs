// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

internal static class LineEndings
{
    public static string ToLf(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    public static string ToCrLf(string text) => ToLf(text).Replace("\n", "\r\n");

    public static string ToCr(string text) => ToLf(text).Replace("\n", "\r");

    public static string ToPlatform(string text) => ToLf(text).Replace("\n", Environment.NewLine);

    public static bool ArePlatformNative(string text) => string.Equals(text, ToPlatform(text), StringComparison.Ordinal);
}
