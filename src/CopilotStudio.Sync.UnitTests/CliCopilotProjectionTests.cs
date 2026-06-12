// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliCopilotProjectionTests
{
    private static readonly LspComponentPathResolver InternalResolver = new();

    [Fact]
    public void AuthoredComponentBodyFolders_AreDerivedFromCliProjectionRules()
    {
        var expected = LspProjection.CliRules.Values
            .Select(r => r.Folder.TrimEnd('/'))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, CliCopilotProjection.AuthoredComponentBodyFolders);
        Assert.Contains("behaviors", CliCopilotProjection.AuthoredComponentBodyFolders);
        Assert.Contains("capabilities/tools", CliCopilotProjection.AuthoredComponentBodyFolders);
        Assert.Contains("capabilities/knowledge", CliCopilotProjection.AuthoredComponentBodyFolders);
        Assert.Contains("capabilities/knowledge/files", CliCopilotProjection.AuthoredComponentBodyFolders);
    }

    [Fact]
    public async Task ComponentProjection_ToolsSkillsKnowledgeAndConnectedAgents_MatchInternalCliResolver()
    {
        var (_, definition, _, _, _) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var tool = definition.Components.OfType<DialogComponent>()
            .First(c => c.Dialog is ConnectorTool or WorkflowTool or McpTool);
        var skill = definition.Components.OfType<DialogComponent>()
            .Single(c => c.Dialog is InlineAgentSkill);
        var knowledge = definition.Components.OfType<KnowledgeSourceComponent>().First();
        var connectedAgentTool = definition.Components.OfType<DialogComponent>()
            .Single(c => c.Dialog is ConnectedAgentTool);

        AssertProjectionMatchesInternalResolver(tool, definition, "capabilities/tools/");
        AssertProjectionMatchesInternalResolver(skill, definition, "behaviors/");
        AssertProjectionMatchesInternalResolver(knowledge, definition, "capabilities/knowledge/");
        AssertProjectionMatchesInternalResolver(connectedAgentTool, definition, "capabilities/tools/");
    }

    [Fact]
    public async Task ComponentProjection_FileAttachments_ReturnBodyAndPayloadPathsFromInternalCliResolver()
    {
        var (_, definition, _, _, _) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var fileAttachment = definition.Components.OfType<FileAttachmentComponent>().Single();

        var projection = AssertProjectionMatchesInternalResolver(
            fileAttachment,
            definition,
            "capabilities/knowledge/files/");

        Assert.Equal("capabilities/knowledge/files", projection.PayloadFolder);
        Assert.Equal(
            $"capabilities/knowledge/files/{fileAttachment.DisplayName}",
            projection.PayloadPath);
    }

    private static CliCopilotComponentProjection AssertProjectionMatchesInternalResolver(
        BotComponentBase component,
        DefinitionBase definition,
        string expectedPathPrefix)
    {
        var projection = CliCopilotProjection.GetComponentProjection(component, definition);
        var expectedPath = InternalResolver.GetComponentPath(component, definition, AuthoringShape.CliCopilot);

        Assert.Equal(expectedPath, projection.BodyPath);
        Assert.Equal(
            new AgentFilePath(expectedPath).ParentDirectoryName.TrimEnd('/', '\\'),
            projection.BodyFolder);
        Assert.StartsWith(expectedPathPrefix, projection.BodyPath, StringComparison.Ordinal);

        return projection;
    }
}
