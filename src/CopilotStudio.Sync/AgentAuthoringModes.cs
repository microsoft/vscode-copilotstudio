// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal enum AgentAuthoringType
{
    Cli = 0,

    Classic = 1,
}

/// <summary>The ids of the authoring modes an agent is scaffolded in, each also the name of its template folder.</summary>
public static class AgentAuthoringModes
{
    /// <summary>The classic authoring mode id.</summary>
    public const string Classic = "classic";

    /// <summary>The CLI-authored agent mode id, and the default.</summary>
    public const string CliCopilot = "cli-copilot";

    private static readonly Dictionary<string, string> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        [Classic] = Classic,
        [CliCopilot] = CliCopilot,
    };

    internal static bool TryParse(string? authoringMode, out AgentAuthoringType authoringType)
    {
        authoringType = AgentAuthoringType.Cli;

        if (authoringMode is null || string.IsNullOrWhiteSpace(authoringMode))
        {
            return true;
        }

        if (!Modes.TryGetValue(authoringMode.Trim(), out var mode))
        {
            return false;
        }

        authoringType = mode == Classic ? AgentAuthoringType.Classic : AgentAuthoringType.Cli;

        return true;
    }
}
