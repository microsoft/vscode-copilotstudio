// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/IComponentPathResolver.cs

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Resolves file paths for ObjectModel components based on LS projection rules.
/// </summary>
internal interface IComponentPathResolver
{
    /// <summary>
    /// Gets the projected relative file path for a component.
    /// </summary>
    /// <param name="component">The component to resolve.</param>
    /// <param name="definition">Optional definition for context (bot/collection name, parent links).</param>
    string GetComponentPath(BotComponentBase component, DefinitionBase? definition = null);
}
