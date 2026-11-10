// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace Microsoft.CopilotStudio.Sync;

internal static class JsonElementReader
{
    public static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? ReadInt32(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    public static DateTimeOffset? ReadTimestamp(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
}
