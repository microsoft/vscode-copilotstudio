// Copyright (C) Microsoft Corporation. All rights reserved.
// Simplified interface from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/IMcsFileParser.cs
// Drops CompileFile(LspDocument<BotElement>) which requires LS-specific types.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Compiles a BotElement model into a BotComponentBase using projection rules.
/// </summary>
internal interface IMcsFileParser
{
    /// <summary>
    /// Compiles the specified agent file and returns the resulting bot component along with any compilation errors
    /// encountered.
    /// </summary>
    /// <param name="relativePath">The relative path to the agent file to be compiled. This path is used to locate the file within the project
    /// structure and must reference a valid agent file.</param>
    /// <param name="fileModel">The model representing the structure and content of the agent file. This parameter defines the elements to be
    /// compiled and influences the resulting bot component.</param>
    /// <param name="context">The projection context that provides additional information and settings for the compilation process. This
    /// context affects how the agent file is processed and compiled.</param>
    /// <returns>A tuple containing the compiled bot component and any exception that occurred during compilation. If compilation
    /// succeeds, the error element will be null.</returns>
    (BotComponentBase? component, Exception? error) CompileFile(AgentFilePath relativePath, BotElement fileModel, ProjectionContext context);

    /// <summary>
    /// Compiles a file model based on the schema name and the BotElement model.
    /// This is useful for compiling content that is not directly associated with a specific document,
    /// e.g. merged content from multiple sources.
    /// </summary>
    /// <param name="schemaName">The schema name associated with the file model. This name is used to identify the schema for the compilation process.</param>
    /// <param name="model">The BotElement model representing the structure and content to be compiled. This parameter defines the elements to be compiled and influences the resulting bot component.</param>
    /// <param name="displayName">An optional display name for the compiled component. This name is used for identification and display purposes.</param>
    /// <param name="description">An optional description for the compiled component. This description provides additional context and information about the component.</param>
    /// <returns>A tuple containing the compiled bot component and any exception that occurred during compilation. If compilation succeeds, the error element will be null.</returns>
    (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model, string? displayName, string? description);
}
