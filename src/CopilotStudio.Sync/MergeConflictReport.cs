// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Conflicted merge output that must be written to disk verbatim for the user to resolve.</summary>
internal sealed class MergeConflictReport
{
    public string? SettingsYaml { get; set; }

    public Dictionary<string, MergeConflictComponent> ComponentsBySchemaName { get; } = new(StringComparer.Ordinal);

    public bool HasConflicts => SettingsYaml != null || ComponentsBySchemaName.Count > 0;
}

/// <summary>A component preserved by an unresolved merge, with the conflicted body to write when the merge produced one.</summary>
internal sealed record MergeConflictComponent(string? Yaml, BotComponentBase Component, McsMetadataConflict? DisplayName = null, McsMetadataConflict? Description = null);

/// <summary>Conflict state produced by a three-way component merge, kept apart from the component's authored text.</summary>
internal readonly record struct ComponentMergeConflict(bool Conflicted, string? Yaml, McsMetadataConflict? DisplayName, McsMetadataConflict? Description);
