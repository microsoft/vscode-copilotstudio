// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/IComponentPathResolver.cs

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Resolves file paths for ObjectModel components based on LS projection rules.
/// </summary>
internal interface IComponentPathResolver
{
    /// <summary>
    /// Gets the projected relative file path for a component, deriving the
    /// <see cref="AuthoringShape"/> from <paramref name="definition"/>'s entity
    /// (TDD D20, D30). This is the single source of truth that keeps write,
    /// read, and delete-detection in agreement: every caller that passes only a
    /// component + definition becomes shape-aware automatically (CLI components
    /// land at the three-layer <c>.mcs.yml</c> paths; classic stays byte-identical).
    /// </summary>
    /// <param name="component">The component to resolve.</param>
    /// <param name="definition">Optional definition for context (bot/collection name, parent links, authoring shape).</param>
    string GetComponentPath(BotComponentBase component, DefinitionBase? definition = null);

    /// <summary>
    /// Gets the projected relative file path for a component using an explicit
    /// <see cref="AuthoringShape"/> (TDD D20). Use this overload only where the
    /// shape is known independently of the definition's entity; otherwise prefer
    /// <see cref="GetComponentPath(BotComponentBase, DefinitionBase?)"/> so the
    /// shape is derived consistently.
    /// </summary>
    string GetComponentPath(BotComponentBase component, DefinitionBase? definition, AuthoringShape shape);
}
