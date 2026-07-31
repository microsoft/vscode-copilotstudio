// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PackagedSkillKnowledgeFileTests
{
    [Fact]
    public async Task GetLocalChangesAsync_FreshLspStyleRecompileFromDisk_CollisionSuffixedSchemas_NoSpuriousCreate()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/packaged-skill-lsp-recompile-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        var skillId = Guid.NewGuid();
        var skill = CreateInlineSkillComponent("cre98_Repro.skill.get-us-weather_e1W", skillId, "get-us-weather");
        var skillMarkdown = CreateFileComponent("cre98_Repro.file.skillmd_49zNm", "./SKILL.md", new BotComponentId(skillId));
        var script = CreateFileComponent("cre98_Repro.file.scriptsgetusweatherps1_GNNvS", "./scripts/Get-UsWeather.ps1", new BotComponentId(skillId));
        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(skillMarkdown), new BotComponentInsert(script) }, botEntity, "token-1"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((folder, _, fileName, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = fileAccessor.OpenWrite(new AgentFilePath($"{GetRelativeFolder(workspace, folder)}/{fileName.Replace('\\', '/')}"));
                var payload = Encoding.UTF8.GetBytes($"payload:{fileName}");
                stream.Write(payload, 0, payload.Length);
                return Task.CompletedTask;
            });
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        // Simulate what the LSP does on a fresh workspace open: recompile every
        // .mcs.yml component file from scratch via the shared file-parser core
        // (no schema override, no cached parent id), instead of reading the
        // cached cloud snapshot, and let ResolveSkillSchemas/GetLocalChanges
        // reconcile the derived (no-suffix) skill schema against the cloud.
        var parser = new SyncMcsFileParser(LspProjectorService.Instance);
        var context = new ProjectionContext(BotName: "cre98_Repro");
        var freshComponents = new List<BotComponentBase>();
        foreach (var key in fileAccessor.Files.Keys.Select(k => k.Replace('\\', '/')).Where(k => k.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase) && k != "settings.mcs.yml"))
        {
            var relativePath = new AgentFilePath(key);
            using var stream = fileAccessor.OpenRead(relativePath);
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();
            var pathWithoutExtension = new AgentFilePath(key.Substring(0, key.Length - ".mcs.yml".Length));
            var model = LspProjectionLayout.TryGetPackagedSkillPayloadTypes(pathWithoutExtension, out _)
                ? CodeSerializer.Deserialize<FileAttachmentComponent>(yaml)
                : CodeSerializer.Deserialize<BotElement>(yaml);
            if (model == null)
            {
                continue;
            }

            var (component, error) = parser.CompileFile(relativePath, model, context, AuthoringShape.CliCopilot);
            Assert.Null(error);
            if (component != null)
            {
                freshComponents.Add(component);
            }
        }

        var freshDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(freshComponents);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, freshDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task Pull_PackagedSkillDeletedInCloud_RemovesLinkAndPrunesFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/packaged-skill-delete-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        var skillId = Guid.NewGuid();
        var skill = CreateInlineSkillComponent("cre98_Repro.skill.pptx_Gev", skillId);
        var skillMarkdown = CreateFileComponent("cre98_Repro.file.skillmd_123", "./SKILL.md", new BotComponentId(skillId));
        var script = CreateFileComponent("cre98_Repro.file.scriptsaddslidepy_456", "./scripts/add_slide.py", new BotComponentId(skillId));
        var retainedSkill = CreateInlineSkillComponent("cre98_Repro.skill.word_Abc", Guid.NewGuid(), "word");
        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(skillMarkdown), new BotComponentInsert(script), new BotComponentInsert(retainedSkill) }, botEntity, "token-1"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx/.skill.json")));

        var cachedDefinition = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentDelete(skill.Id, skill.Version), new BotComponentDelete(skillMarkdown.Id, skillMarkdown.Version), new BotComponentDelete(script.Id, script.Version) }, botEntity, "token-2"));

        await synchronizer.PullExistingChangesAsync(workspace, operationContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.DoesNotContain(fileAccessor.Files.Keys, path => path.StartsWith("behaviors/pptx/", StringComparison.Ordinal));
        Assert.False(fileAccessor.Exists(new AgentFilePath("behaviors/pptx.mcs.yml")));
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/word.mcs.yml")));
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/word/.skill.json")));
    }

    [Fact]
    public async Task GetLocalChangesAsync_PackagedSkillAnchorDeletedButPayloadRemains_EmitsSkillDeleteNotCreate()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/packaged-skill-anchor-delete-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        var skillId = Guid.NewGuid();
        var skill = CreateInlineSkillComponent("cre98_Repro.skill.pptx_Gev", skillId);
        var skillMarkdown = CreateFileComponent("cre98_Repro.file.skillmd_123", "./SKILL.md", new BotComponentId(skillId));
        var script = CreateFileComponent("cre98_Repro.file.scriptsaddslidepy_456", "./scripts/add_slide.py", new BotComponentId(skillId));
        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(skillMarkdown), new BotComponentInsert(script) }, botEntity, "token-1"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());
        var downloadGate = new object();
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx.mcs.yml")));

        fileAccessor.Delete(new AgentFilePath("behaviors/pptx.mcs.yml"));
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx/SKILL.md")));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        Assert.DoesNotContain(read.Components, c => c.SchemaNameString == skill.SchemaNameString);

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, read, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Delete && change.SchemaName == skill.SchemaNameString);
        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == skill.SchemaNameString);
        Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentDelete>());
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx/SKILL.md")));
    }

    [Fact]
    public async Task GetLocalChangesAsync_NaturallyNamedSkillAnchorDeletedButPayloadRemains_EmitsSkillDeleteNotCreate()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/packaged-skill-natural-anchor-delete-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        var skillId = Guid.NewGuid();
        var skill = CreateInlineSkillComponent("cre98_Repro.skill.pptx", skillId);
        var skillMarkdown = CreateFileComponent("cre98_Repro.file.skillmd_123", "./SKILL.md", new BotComponentId(skillId));
        var script = CreateFileComponent("cre98_Repro.file.scriptsaddslidepy_456", "./scripts/add_slide.py", new BotComponentId(skillId));
        mockIsland.Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(skill), new BotComponentInsert(skillMarkdown), new BotComponentInsert(script) }, botEntity, "token-1"));
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());
        var downloadGate = new object();
        mockDataverse.Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx.mcs.yml")));

        // Naturally-derived schema: the writer would previously have deleted the
        // sidecar here since it's redundant, leaving no discriminator on delete.
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx/.skill.json")));

        fileAccessor.Delete(new AgentFilePath("behaviors/pptx.mcs.yml"));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        Assert.DoesNotContain(read.Components, c => c.SchemaNameString == skill.SchemaNameString);

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, read, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Delete && change.SchemaName == skill.SchemaNameString);
        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == skill.SchemaNameString);
        Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentDelete>());
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/pptx/SKILL.md")));
    }

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
        Assert.Contains("behaviors/pptx.mcs.yml", keys);
        Assert.Contains("behaviors/pptx/.skill.json", keys);
        Assert.Contains("behaviors/pptx/skillmd_123.mcs.yml", keys);
        Assert.Contains("behaviors/pptx/scriptsaddslidepy_456.mcs.yml", keys);
        Assert.Contains("behaviors/pptx/SKILL.md", keys);
        Assert.Contains("behaviors/pptx/scripts/add_slide.py", keys);

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

    [Fact]
    public async Task PushLocalChangesAsync_NewBareSkillFolder_CreatesSkillInCloud()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-push-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var skill = finalCache.Components.OfType<DialogComponent>().SingleOrDefault(component => component.Dialog is InlineAgentSkill);
        Assert.NotNull(skill);
        Assert.True(fileAccessor.Exists(new AgentFilePath("behaviors/get-us-weather-2.mcs.yml")));

        var skillMdSidecars = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-weather-2/SKILL.md", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        Assert.Single(skillMdSidecars);
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>());
    }

    [Fact]
    public async Task PushLocalChangesAsync_NewBareSkillFolder_NestedPayloadFile_DoesNotDuplicateAcrossPasses()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-nested-push-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1"), "script body\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var fileComponents = finalCache.Components.OfType<FileAttachmentComponent>().ToList();
        Assert.Equal(2, fileComponents.Count);
        Assert.Single(fileComponents, component => component.DisplayName == "SKILL.md");
        Assert.Single(fileComponents, component => component.DisplayName == "scripts/Get-UsWeather.ps1");

        var scriptSidecars = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-weather-2/scriptsGet-UsWeather.ps1", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        Assert.Single(scriptSidecars);
    }

    [Fact]
    public async Task PushLocalChangesAsync_NewPayloadNameIsPrefixOfExistingSidecar_DoesNotStealSchema()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/prefix-sidecar-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/foo_bar"), "bar body\n", CancellationToken.None);
        await synchronizer.PushLocalChangesAsync(workspace, operationContext, new BotDefinition().WithEntity(botEntity), mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var frozenLspModel = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var fooBarSchemaAfterFirstPush = frozenLspModel.Components.OfType<FileAttachmentComponent>().Single(component => component.DisplayName == "foo_bar").SchemaNameString;

        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/foo"), "foo body\n", CancellationToken.None);
        await synchronizer.PushLocalChangesAsync(workspace, operationContext, frozenLspModel, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var fileComponents = finalCache.Components.OfType<FileAttachmentComponent>().ToList();

        Assert.Equal(3, fileComponents.Count);
        var foo = Assert.Single(fileComponents, component => component.DisplayName == "foo");
        var fooBar = Assert.Single(fileComponents, component => component.DisplayName == "foo_bar");
        Assert.Equal("cre98_Repro.file.foo", foo.SchemaNameString);
        Assert.Equal(fooBarSchemaAfterFirstPush, fooBar.SchemaNameString);
        Assert.NotEqual(fooBar.SchemaNameString, foo.SchemaNameString);
    }

    [Fact]
    public async Task PushLocalChangesAsync_AddBareSkillWhenAnotherSkillExists_FrozenLspModel_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/second-bare-skill-frozen-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-temperature/SKILL.md"), "temperature body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-temperature/scripts/Get-UsTemperature.ps1"), "temperature script\n", CancellationToken.None);
        await synchronizer.PushLocalChangesAsync(workspace, operationContext, new BotDefinition().WithEntity(botEntity), mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var frozenLspModel = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;

        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "weather body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1"), "weather script\n", CancellationToken.None);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, frozenLspModel, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Contains(finalCache.Components.OfType<DialogComponent>(), component => component.DisplayName == "get-us-weather-2");
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "scripts/Get-UsWeather.ps1");
        Assert.Single(finalCache.Components.OfType<DialogComponent>(), component => component.DisplayName == "get-us-temperature");
    }

    [Fact]
    public async Task PushLocalChangesAsync_SecondPushWithStaleEmptyDefinition_DoesNotDuplicateSkillMd()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-push-repeat-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);
        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var skillMdSidecars = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-weather-2/SKILL.md", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        Assert.Single(skillMdSidecars);
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>());
    }

    [Fact]
    public async Task PushLocalChangesAsync_SecondSkillAddedLater_DoesNotDuplicateFirstSkillPayload()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/two-skill-push-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-temperature/SKILL.md"), "skill body 1\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body 2\n", CancellationToken.None);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);
        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var skills = finalCache.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill).ToList();
        Assert.Equal(2, skills.Count);
        var skill1 = skills.Single(component => component.DisplayName == "get-us-temperature");
        var skill1FileComponents = finalCache.Components.OfType<FileAttachmentComponent>().Where(component => component.ParentBotComponentId == skill1.Id).ToList();
        Assert.Single(skill1FileComponents);
        var skill1MdSidecars = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-temperature/SKILL.md", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        Assert.Single(skill1MdSidecars);
    }

    [Fact]
    public async Task PushLocalChangesAsync_SecondSkillAddedLater_WithCaughtUpDefinitionForFirstSkill_DoesNotDuplicatePayload()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/two-skill-push-caughtup-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-temperature/SKILL.md"), "skill body 1\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var emptyDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, emptyDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var caughtUpDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body 2\n", CancellationToken.None);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, caughtUpDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);
        await synchronizer.PushLocalChangesAsync(workspace, operationContext, caughtUpDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var skills = finalCache.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill).ToList();
        Assert.Equal(2, skills.Count);
        var skill1 = skills.Single(component => component.DisplayName == "get-us-temperature");
        var skill1FileComponents = finalCache.Components.OfType<FileAttachmentComponent>().Where(component => component.ParentBotComponentId == skill1.Id).ToList();
        Assert.Single(skill1FileComponents);
        var skill1MdSidecars2 = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-temperature/SKILL.md", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        Assert.Single(skill1MdSidecars2);
    }

    [Fact]
    public async Task PushLocalChangesAsync_TwoNewBareSkillFoldersInSinglePush_DoesNotOrphanSidecarMetadata()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/two-new-skills-single-push-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-temperature/SKILL.md"), "skill body 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body 2\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var skills = finalCache.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill).ToList();
        Assert.Equal(2, skills.Count);

        var temperatureSidecars = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-temperature/", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        var weatherSidecars = fileAccessor.Files.Keys.Where(key => key.Replace('\\', '/').StartsWith("behaviors/get-us-weather-2/", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal)).ToList();
        Assert.Single(temperatureSidecars);
        Assert.Single(weatherSidecars);

        var temperatureSkill = skills.Single(component => component.DisplayName == "get-us-temperature");
        var weatherSkill = skills.Single(component => component.DisplayName == "get-us-weather-2");
        var temperatureFileComponents = finalCache.Components.OfType<FileAttachmentComponent>().Where(component => component.ParentBotComponentId == temperatureSkill.Id).ToList();
        var weatherFileComponents = finalCache.Components.OfType<FileAttachmentComponent>().Where(component => component.ParentBotComponentId == weatherSkill.Id).ToList();
        Assert.Single(temperatureFileComponents);
        Assert.Single(weatherFileComponents);
    }

    [Fact]
    public async Task PushLocalChangesAsync_CloudReassignsSchemaOnInsert_BareSkillWithScript_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-cloud-suffix-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "weather body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1"), "weather script\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewIdAndCloudSuffixSchema(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Single(finalCache.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "scripts/Get-UsWeather.ps1");
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "SKILL.md");
    }

    [Fact]
    public async Task GetLocalChangesAsync_StaleSkillLink_CloudSkillMissing_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/stale-link-preview-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { CreateInlineSkillComponent("cre98_Repro.skill.get-us-temperature", Guid.NewGuid(), "get-us-temperature") }));
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2.mcs.yml"), "mcs.metadata:\n  componentName: get-us-weather-2\nkind: InlineAgentSkill\ncontent: placeholder\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/.skill.json"), "{ \"schemaName\": \"cre98_Repro.skill.get-us-weather-2\", \"folderName\": \"get-us-weather-2\" }", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "weather body\n", CancellationToken.None);

        var localDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            CreateInlineSkillComponent("cre98_Repro.skill.get-us-weather-2", Guid.NewGuid(), "get-us-weather-2"),
        });

        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Contains(changes, change => change.SchemaName == "cre98_Repro.skill.get-us-weather-2");
    }

    [Fact]
    public async Task PushLocalChangesAsync_StaleSkillLink_CloudSkillMissing_ReCreatesSkillDoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/stale-link-push-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { CreateInlineSkillComponent("cre98_Repro.skill.get-us-temperature", Guid.NewGuid(), "get-us-temperature") }));
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2.mcs.yml"), "mcs.metadata:\n  componentName: get-us-weather-2\nkind: InlineAgentSkill\ncontent: placeholder\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/.skill.json"), "{ \"schemaName\": \"cre98_Repro.skill.get-us-weather-2\", \"folderName\": \"get-us-weather-2\" }", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "weather body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1"), "weather script\n", CancellationToken.None);

        var staleSkillId = Guid.NewGuid();
        var localDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            CreateInlineSkillComponent("cre98_Repro.skill.get-us-weather-2", staleSkillId, "get-us-weather-2"),
            CreateFileComponent("cre98_Repro.file.get-us-weather-2.SKILL.md", "SKILL.md", new BotComponentId(staleSkillId)),
            CreateFileComponent("cre98_Repro.file.scriptsGet-UsWeather.ps1", "scripts/Get-UsWeather.ps1", new BotComponentId(staleSkillId)),
        });

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, localDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Single(finalCache.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill && component.DisplayName == "get-us-weather-2");
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "SKILL.md");
        Assert.Single(finalCache.Components.OfType<FileAttachmentComponent>(), component => component.DisplayName == "scripts/Get-UsWeather.ps1");
    }

    [Fact]
    public async Task ListKnowledgeFilesAsync_SkillPayloadFiles_ExcludedFromKnowledgeList()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-payload-knowledge-list-{Guid.NewGuid():N}/");
        var skillId = Guid.NewGuid();
        var skill = CreateInlineSkillComponent("cre98_Repro.skill.pptx_Gev", skillId);
        var skillPayload = CreateFileComponent("cre98_Repro.file.skillmd_123", "SKILL.md", new BotComponentId(skillId));
        var rootKnowledgeBuilder = new FileAttachmentComponent()
            .WithSchemaName("cre98_Repro.file.rootknowledge")
            .WithDisplayName("root.txt")
            .WithDescription("root knowledge")
            .ToBuilder();
        rootKnowledgeBuilder.Id = Guid.NewGuid();
        var cloudCache = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!)
            .WithComponents(new BotComponentBase[] { skill, skillPayload, rootKnowledgeBuilder.Build() });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), cloudCache);

        var listed = await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None);

        var schemaNames = listed.Select(info => info.SchemaName).ToList();
        Assert.Contains("cre98_Repro.file.rootknowledge", schemaNames);
        Assert.DoesNotContain("cre98_Repro.file.skillmd_123", schemaNames);
    }

    [Fact]
    public async Task PushLocalChangesAsync_BareSkillFolderWithPunctuation_UploadsPayloadsFromOriginalFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-punct-push-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/weather.v2/SKILL.md"), "skill body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/weather.v2/scripts/Get-Weather.ps1"), "script body\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var fileComponents = finalCache.Components.OfType<FileAttachmentComponent>().ToList();
        Assert.Equal(2, fileComponents.Count);
        Assert.Single(fileComponents, component => component.DisplayName == "SKILL.md");
        Assert.Single(fileComponents, component => component.DisplayName == "scripts/Get-Weather.ps1");

        Assert.Contains(fileAccessor.Files.Keys, key => key.Replace('\\', '/').StartsWith("behaviors/weather.v2/", StringComparison.Ordinal) && key.EndsWith(".mcs.yml", StringComparison.Ordinal));
        Assert.DoesNotContain(fileAccessor.Files.Keys, key => key.Replace('\\', '/').StartsWith("behaviors/weatherv2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLocalChangesAsync_AfterBareSkillPush_StaleWorkspaceDefinition_ReportsNoSkillDeletion()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-postpush-changes-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/SKILL.md"), "skill body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/get-us-weather-2/scripts/Get-UsWeather.ps1"), "script body\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewId(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var staleDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, staleDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, staleDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.DoesNotContain(changes, change => change.ChangeType == ChangeType.Delete);
    }

    [Fact]
    public async Task PushLocalChangesAsync_BareSkillPunctuationFolder_CloudSuffixedSchema_KeepsFilesInOriginalFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-punct-cloudsuffix-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/weather.v2/SKILL.md"), "skill body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/weather.v2/scripts/Get-Weather.ps1"), "script body\n", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change => change is BotComponentInsert insert && insert.Component is BotComponentBase component ? new BotComponentInsert(AssignNewIdAndCloudSuffixSchema(component)) : change);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var cloudSkill = Assert.Single(finalCache.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);
        Assert.EndsWith("_cLd", cloudSkill.SchemaNameString, StringComparison.Ordinal);
        Assert.Equal(2, finalCache.Components.OfType<FileAttachmentComponent>().Count(component => component.ParentBotComponentId == cloudSkill.Id));

        var projectedPaths = fileAccessor.Files.Keys.Select(key => key.Replace('\\', '/')).ToList();
        Assert.Contains("behaviors/weather.v2.mcs.yml", projectedPaths);
        Assert.Contains("behaviors/weather.v2/.skill.json", projectedPaths);
        Assert.Contains(projectedPaths, path => path.StartsWith("behaviors/weather.v2/", StringComparison.Ordinal) && path.Contains("SKILL.md", StringComparison.Ordinal) && path.EndsWith(".mcs.yml", StringComparison.Ordinal));
        Assert.Contains(projectedPaths, path => path.StartsWith("behaviors/weather.v2/", StringComparison.Ordinal) && path.Contains("Get-Weather.ps1", StringComparison.Ordinal) && path.EndsWith(".mcs.yml", StringComparison.Ordinal));
        Assert.DoesNotContain(projectedPaths, path => path.StartsWith("behaviors/weatherv2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLocalChangesAsync_ClassicAgent_BareSkillFolder_NotSynthesized()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/classic-bare-skill-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123_classic\n")!;
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/foo/SKILL.md"), "skill body\n", CancellationToken.None);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, new BotDefinition().WithEntity(botEntity), mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Empty(changeSet.BotComponentChanges);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task GetLocalChangesAsync_BareSkillFoldersWithNonAlphanumericNames_ProduceDistinctNonEmptyBundleMarkers()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/bare-skill-bundle-collision-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_Repro\ntemplate: cliagent-1.0.0\n")!;
        await fileAccessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath("settings.mcs.yml"),
            "displayName: Repro\nschemaName: cre98_Repro\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n  agentSettings:\n    model:\n      series: Sonnet46\n    instructions:\n      segments:\n        - kind: StaticSegment\n          value: Test.\ntemplate: cliagent-1.0.0\nlanguage: 1033\n",
            CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/---/SKILL.md"), "skill body\n", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("behaviors/___/SKILL.md"), "skill body\n", CancellationToken.None);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, new BotDefinition().WithEntity(botEntity), mockDataverse.Object, syncInfo, CancellationToken.None);

        var skillContents = changeSet.BotComponentChanges
            .OfType<BotComponentInsert>()
            .Select(insert => insert.Component)
            .OfType<DialogComponent>()
            .Select(dialog => dialog.Dialog)
            .OfType<InlineAgentSkill>()
            .Select(skill => skill.Content ?? string.Empty)
            .ToList();

        Assert.Equal(2, skillContents.Count);
        Assert.All(skillContents, content => Assert.DoesNotContain($"{LspProjection.FileAttachmentInfix}zip", content));
        Assert.Equal(2, skillContents.Distinct().Count());
    }

    private static BotComponentBase AssignNewId(BotComponentBase component)
    {
        var builder = component.ToBuilder();
        builder.Id = Guid.NewGuid();
        return builder.Build();
    }

    private static BotComponentBase AssignNewIdAndCloudSuffixSchema(BotComponentBase component)
    {
        var suffixed = component switch
        {
            DialogComponent dialog => (BotComponentBase)dialog.WithSchemaName(new DialogSchemaName(dialog.SchemaNameString + "_cLd")),
            FileAttachmentComponent file => file.WithSchemaName(file.SchemaNameString + "_cLd"),
            _ => component,
        };
        var builder = suffixed.ToBuilder();
        builder.Id = Guid.NewGuid();
        return builder.Build();
    }

    private static string GetRelativeFolder(DirectoryPath workspace, string folder)
    {
        var root = workspace.ToString().TrimEnd('\\', '/').Replace('\\', '/');
        var normalizedFolder = folder.TrimEnd('\\', '/').Replace('\\', '/');
        return normalizedFolder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? normalizedFolder.Substring(root.Length + 1)
            : normalizedFolder;
    }

    private static DialogComponent CreateInlineSkillComponent(string schemaName, Guid id, string displayName = "pptx")
    {
        var dialog = (DialogBase)CodeSerializer.Deserialize<BotElement>(
            "kind: InlineAgentSkill\ncontent: <!-- bic:bundle=cre98_Repro.file.pptxzip_Aq-pc -->\n")!;

        return new DialogComponent(
            schemaName: schemaName,
            displayName: displayName,
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
