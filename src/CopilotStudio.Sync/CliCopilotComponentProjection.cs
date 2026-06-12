// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Authored CliCopilot workspace paths for a component.
/// </summary>
public sealed record CliCopilotComponentProjection
{
    public string BodyPath { get; init; } = string.Empty;

    public string BodyFolder { get; init; } = string.Empty;

    public string? PayloadPath { get; init; }

    public string? PayloadFolder { get; init; }
}
