// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PackagedSkillKnowledgeFileTests
{
    [Fact]
    public async Task CloneChanges_PackagedSkill_DownloadsPayloadFilesAndDoesNotRediscoverThem()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/packaged-skill-clone-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var botEntity = CodeSerializer.Deserialize<BotEntity>(
            "kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        var skillId = Guid.NewGuid();
        var skill = CreateInlineSkillComponent("cre98_Repro.skill.pptx_Gev", skillId);
        var skillMarkdown = CreateFileComponent("cre98_Repro.file.skillmd_123", "./SKILL.md", new BotComponentId(skillId));
        var script = CreateFileComponent("cre98_Repro.file.scriptsaddslidepy_456", "./scripts/add_slide.py", new BotComponentId(skillId));

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(
                new BotComponentChange[]
                {
                    new BotComponentInsert(skill),
                    new BotComponentInsert(skillMarkdown),
                    new BotComponentInsert(script),
                },
                botEntity,
                "token-1"));

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        var downloadGate = new object();
        mockDataverse
            .Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((folder, _, fileName, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (downloadGate)
                {
                    using var stream = fileAccessor.OpenWrite(new AgentFilePath($"{GetRelativeFolder(workspace, folder)}/{fileName.Replace('\\', '/')}"));
                    var payload = Encoding.UTF8.GetBytes($"payload:{fileName}");
                    stream.Write(payload, 0, payload.Length);
                }

                return Task.CompletedTask;
            });

        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None);

        var keys = fileAccessor.Files.Keys.Select(k => k.Replace('\\', '/')).ToList();
        Assert.Contains("behaviors/pptx_Gev/skillmd_123.mcs.yml", keys);
        Assert.Contains("behaviors/pptx_Gev/scriptsaddslidepy_456.mcs.yml", keys);
        Assert.Contains("behaviors/pptx_Gev/SKILL.md", keys);
        Assert.Contains("behaviors/pptx_Gev/scripts/add_slide.py", keys);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var fileComponents = read.Components.OfType<FileAttachmentComponent>().ToList();
        Assert.Equal(2, fileComponents.Count);
        Assert.Contains(fileComponents, c => c.SchemaNameString == skillMarkdown.SchemaNameString);
        Assert.Contains(fileComponents, c => c.SchemaNameString == script.SchemaNameString);

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(
            workspace,
            new Mock<ISyncDataverseClient>(MockBehavior.Strict).Object,
            CancellationToken.None);
        Assert.Empty(uploaded);
    }

    private static string GetRelativeFolder(DirectoryPath workspace, string folder)
    {
        var root = workspace.ToString().TrimEnd('\\', '/').Replace('\\', '/');
        var normalizedFolder = folder.TrimEnd('\\', '/').Replace('\\', '/');
        return normalizedFolder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? normalizedFolder.Substring(root.Length + 1)
            : normalizedFolder;
    }

    private static DialogComponent CreateInlineSkillComponent(string schemaName, Guid id)
    {
        var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>(
            "kind: InlineAgentSkill\ncontent: <!-- bic:bundle=cre98_Repro.file.pptxzip_Aq-pc -->\n")!;

        return new DialogComponent(
            schemaName: schemaName,
            displayName: "pptx",
            description: "Packaged skill",
            id: id,
            parentBotComponentId: default,
            dialog: dialog);
    }

    private static FileAttachmentComponent CreateFileComponent(string schemaName, string displayName, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("Packaged skill file")
            .ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }
}
