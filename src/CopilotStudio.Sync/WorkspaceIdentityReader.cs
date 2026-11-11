// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The identity and authoring shape an agent workspace declares for itself.</summary>
/// <param name="SchemaName">The agent's schema name.</param>
/// <param name="DisplayName">The agent's display name, or its schema name when it declares none.</param>
/// <param name="Description">The agent's description, when the identity file declares one.</param>
/// <param name="AuthoringShape">The shape the workspace was authored as.</param>
public sealed record WorkspaceIdentity(string SchemaName, string DisplayName, string? Description, AuthoringShape AuthoringShape);

/// <summary>Reads the identity an agent workspace declares in its settings file or its classic agent file.</summary>
public static class WorkspaceIdentityReader
{
    private const string DescriptionKey = "description";

    private static readonly AgentFilePath ClassicAgentPath = new("agent.mcs.yml");

    /// <summary>Reads the identity a workspace folder declares.</summary>
    /// <param name="workspaceFolder">The folder holding the agent workspace.</param>
    /// <returns>The declared identity, or <see langword="null"/> when the folder declares no readable identity.</returns>
    public static WorkspaceIdentity? Read(string workspaceFolder)
    {
        var fileAccessor = WorkflowWorkspace.OpenWorkspace(workspaceFolder);

        return IdentityOf(fileAccessor, CliAgentBotEntityReader.SettingsPath) ?? IdentityOf(fileAccessor, ClassicAgentPath);
    }

    private static WorkspaceIdentity? IdentityOf(IFileAccessor fileAccessor, AgentFilePath path)
    {
        if (!fileAccessor.Exists(path))
        {
            return null;
        }

        BotEntity entity;

        try
        {
            entity = CliAgentBotEntityReader.Read(fileAccessor, path);
        }
        catch (WorkspaceValidationException)
        {
            return null;
        }

        var schemaName = entity.SchemaName.Value;

        return string.IsNullOrWhiteSpace(schemaName) ? null : new WorkspaceIdentity(schemaName, entity.DisplayName is { } displayName && !string.IsNullOrWhiteSpace(displayName) ? displayName : schemaName, entity.ExtensionData?.Properties.TryGetValue(DescriptionKey, out var description) == true && description is StringDataValue text ? text.Value : null, AgentClassifier.DetectAuthoringShapeFromFolder(fileAccessor, entity));
    }
}
