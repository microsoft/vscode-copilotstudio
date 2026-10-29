// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace Microsoft.CopilotStudio.McsCore;

internal static class SyncStateMap
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    internal static Dictionary<string, string> Read(IFileAccessor fileAccessor, AgentFilePath path)
    {
        try
        {
            if (fileAccessor.Exists(path))
            {
                using var stream = fileAccessor.OpenRead(path);
                if (JsonSerializer.Deserialize<Dictionary<string, string>>(stream) is { } parsed)
                {
                    return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception)
        {
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static void Write(IFileAccessor fileAccessor, AgentFilePath path, IReadOnlyDictionary<string, string> state)
    {
        try
        {
            using var stream = fileAccessor.OpenWrite(path);
            JsonSerializer.Serialize(stream, state, IndentedJsonOptions);
        }
        catch (Exception)
        {
        }
    }
}
