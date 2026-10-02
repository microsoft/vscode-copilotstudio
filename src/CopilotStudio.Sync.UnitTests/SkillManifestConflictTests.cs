// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SkillManifestConflictTests
{
    private const string CliSettings =
        "displayName: NA Test\n" +
        "schemaName: cr123_natest\n" +
        "configuration:\n" +
        "  recognizer:\n" +
        "    kind: CLICopilotRecognizer\n" +
        "  agentSettings:\n" +
        "    model:\n" +
        "      series: Sonnet46\n" +
        "template: cliagent-1.0.0\n" +
        "language: 1033\n";

    private const string ConflictedManifest =
        "---\n" +
        "name: skill-1\n" +
        "description: description of skill 1\n" +
        "---\n" +
        "<<<<<<< (Current Change)\n" +
        "Local 1 - When this skill is activated:\n" +
        "=======\n" +
        "Cloud 2 - When this skill is activated:\n" +
        ">>>>>>> (Incoming Change)\n" +
        "\n" +
        "1. [First step or action the agent should take]\n";

    private const string CleanManifest =
        "---\n" +
        "name: skill-1\n" +
        "description: description of skill 1\n" +
        "---\n" +
        "Local 1 - When this skill is activated:\n";

    private const string LiteralMarkerManifest =
        "---\n" +
        "name: skill-1\n" +
        "description: description of skill 1\n" +
        "---\n" +
        "A git conflict starts with an indented marker:\n" +
        "\n" +
        "  <<<<<<< ours\n" +
        "\n" +
        "and ends with:\n" +
        "\n" +
        "  >>>>>>> theirs\n";

    private const string IndentedConflictManifest =
        "---\n" +
        "name: skill-1\n" +
        "description: description of skill 1\n" +
        "---\n" +
        "  <<<<<<< (Current Change)\n" +
        "  Local 1 - When this skill is activated:\n" +
        "  =======\n" +
        "  Cloud 2 - When this skill is activated:\n" +
        "  >>>>>>> (Incoming Change)\n";

    private static async Task<(WorkspaceSynchronizer Sync, InMemoryFileAccessor Accessor, DirectoryPath Workspace)> CreateWorkspaceAsync()
    {
        var (synchronizer, factory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/skill-conflict-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);
        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);

        mockIsland
            .Setup(island => island.SaveChangesAsync(It.IsAny<Agents.Platform.Content.AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<Agents.Platform.Content.AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) => Task.FromResult(new PvaComponentChangeSet(incoming.BotComponentChanges, incoming.Bot ?? CloudDefinition().Entity, Guid.NewGuid().ToString("N"))));

        return (synchronizer, accessor, workspace);
    }

    private static void Write(InMemoryFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedSkillManifest_Throws()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Contains("Unresolved merge conflict", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(5, diagnostic.Line);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedSkillManifest_NamesFileInExceptionMessage()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        Assert.Contains("behaviors/skill-1/SKILL.md(5,1)", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Show <<<<<<< and >>>>>>> in the example.\n")]
    public async Task ReadWorkspaceDefinition_CleanSkillManifest_DoesNotThrow(string instructions)
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-1/SKILL.md", CleanManifest + instructions);

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Contains(read.Components, component => component is DialogComponent { Dialog: InlineAgentSkill });
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedSkillManifest_ReportsEveryConflictedSkill()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);
        Write(accessor, "behaviors/skill-2/SKILL.md", ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        Assert.Equal(2, failure.Diagnostics.Length);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedPackagedSkillManifest_Throws()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WritePackagedSkill(accessor, ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Fact]
    public async Task Push_ConflictedPackagedSkillManifest_IsBlockedBeforeItIsUploaded()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        WritePackagedSkill(accessor, "Instructions.\n");

        var cachedDefinition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => PushAsync(sync, workspace, cachedDefinition));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Theory]
    [InlineData("Instructions.\n")]
    [InlineData("Show <<<<<<< and >>>>>>> in the example.\n")]
    public async Task Push_CleanPackagedSkillManifest_IsNotBlocked(string instructions)
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        WritePackagedSkill(accessor, instructions);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        await PushAsync(sync, workspace, definition);
    }

    private static void WritePackagedSkill(InMemoryFileAccessor accessor, string manifest)
    {
        Write(accessor, "behaviors/skill-1/skill.mcs.yml", "mcs.metadata:\n  componentName: skill-1\n  schemaName: cr123_natest.skill.skill-1\n  bundle: cr123_natest.file.skill1zip\n  manifestSchemaName: cr123_natest.file.skill1.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/skill-1/SKILL.md", manifest);
    }

    private static void WriteAttachmentBackedSkill(InMemoryFileAccessor accessor, string manifest)
    {
        Write(accessor, "behaviors/skill-1/skill.mcs.yml", "mcs.metadata:\n  componentName: skill-1\n  schemaName: cr123_natest.skill.skill-1\n  manifestSchemaName: cr123_natest.file.skill1.SKILL.md\nkind: InlineAgentSkill\n");
        Write(accessor, "behaviors/skill-1/SKILL.md", manifest);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedAttachmentBackedManifest_Throws()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WriteAttachmentBackedSkill(accessor, ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Fact]
    public async Task Push_ConflictedAttachmentBackedManifest_IsBlocked()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        WriteAttachmentBackedSkill(accessor, CleanManifest);

        var cachedDefinition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => PushAsync(sync, workspace, cachedDefinition));

        Assert.Equal("behaviors/skill-1/SKILL.md", Assert.Single(failure.Diagnostics).FilePath);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_ConflictedManifest_IsBlockedBeforeAnyUpload()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WriteAttachmentBackedSkill(accessor, CleanManifest);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        WorkspaceSynchronizer.WriteCloudCache(accessor, definition);

        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var dataverse = new Mock<Dataverse.ISyncDataverseClient>(MockBehavior.Strict);
        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None));

        Assert.Equal("behaviors/skill-1/SKILL.md", Assert.Single(failure.Diagnostics).FilePath);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_CleanManifest_IsNotBlocked()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WriteAttachmentBackedSkill(accessor, CleanManifest);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        WorkspaceSynchronizer.WriteCloudCache(accessor, definition);

        Assert.Null(await Record.ExceptionAsync(() => sync.UploadKnowledgeFilesAsync(workspace, new Mock<Dataverse.ISyncDataverseClient>().Object, CancellationToken.None)));
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_LiteralMarkerTextInManifest_DoesNotThrow()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-1/SKILL.md", LiteralMarkerManifest);

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Contains(read.Components, component => component is DialogComponent { Dialog: InlineAgentSkill });
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_IndentedConflictInManifest_Throws()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/skill-1/SKILL.md", IndentedConflictManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        Assert.Equal(5, diagnostic.Line);
    }

    [Fact]
    public async Task Push_LiteralMarkerTextInManifest_IsNotBlocked()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        WritePackagedSkill(accessor, LiteralMarkerManifest);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        sync.ThrowIfWorkspaceInvalid(workspace, definition);
        await PushAsync(sync, workspace, definition);
    }

    [Fact]
    public async Task ThrowIfWorkspaceInvalid_ConflictedManifest_BlocksWithoutPushing()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        WritePackagedSkill(accessor, CleanManifest);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var failure = Assert.Throws<WorkspaceValidationException>(() => sync.ThrowIfWorkspaceInvalid(workspace, definition));

        Assert.Equal("behaviors/skill-1/SKILL.md", Assert.Single(failure.Diagnostics).FilePath);
    }

    [Fact]
    public async Task ThrowIfWorkspaceInvalid_CleanManifest_DoesNotBlock()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        WritePackagedSkill(accessor, CleanManifest);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        sync.ThrowIfWorkspaceInvalid(workspace, definition);
    }

    private static BotDefinition CloudDefinition()
        => new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\n" + CliSettings)!);

    private static Task PushAsync(WorkspaceSynchronizer sync, DirectoryPath workspace, DefinitionBase definition) => sync.PushLocalChangesAsync(
        workspace,
        ComponentWriterDefensiveTests.CreateMockOperationContext(),
        definition,
        new Mock<Dataverse.ISyncDataverseClient>().Object,
        new AgentSyncInfo { AgentId = Guid.NewGuid() },
        cloudFlowMetadata: null,
        ImmutableArray<Dataverse.SyncDataverseClient.AIPromptMetadata>.Empty,
        CancellationToken.None);

    [Fact]
    public async Task Push_ConflictedSkillManifest_IsBlockedEvenWhenTheDefinitionIsCached()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        Write(accessor, "behaviors/skill-1/SKILL.md", CleanManifest);

        var cachedDefinition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Write(accessor, "behaviors/skill-1/SKILL.md", ConflictedManifest);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => PushAsync(sync, workspace, cachedDefinition));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Fact]
    public async Task Push_RepeatedSkillWithoutManifestFile_DoesNotHideConflictedContent()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        var skill = new DialogComponent(
            schemaName: "cr123_natest.skill.skill-1",
            displayName: "skill-1",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new InlineAgentSkill.Builder { Content = CleanManifest }.Build());
        var definition = CloudDefinition().WithComponents(new[] { skill, SkillBodyProjection.WithContent(skill, ConflictedManifest) });

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => PushAsync(sync, workspace, definition));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("behaviors/skill-1/SKILL.md", diagnostic.FilePath);
        Assert.Equal(5, diagnostic.Line);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Fact]
    public async Task Push_ConflictedUnknownComponentFile_DoesNotUploadIt()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());

        var cleanDefinition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Write(accessor, "topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n");

        await PushAsync(sync, workspace, cleanDefinition);

        Assert.DoesNotContain("<<<<<<<", ReadAll(accessor, ".mcs/botdefinition.json"), StringComparison.Ordinal);
    }

    private static string ReadAll(InMemoryFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Push_CleanWorkspace_IsNotBlocked()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        Write(accessor, "behaviors/skill-1/SKILL.md", CleanManifest);

        var definition = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        await PushAsync(sync, workspace, definition);
    }

    [Theory]
    [InlineData("displayName: one\ndisplayName: two\n", "Duplicate key")]
    [InlineData("displayName: Agent\nconfiguration:\n\trecognizer: x\n", "Tab")]
    [InlineData("displayName: \"unterminated\n", "quoted")]
    public async Task ReadWorkspaceDefinition_MalformedSettings_IsBlocked(string malformedYaml, string expectedMessageFragment)
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), malformedYaml, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        Assert.Contains(expectedMessageFragment, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedSettings_IsBlocked()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), "<<<<<<< ours\n" + CliSettings + "=======\n" + CliSettings + ">>>>>>> theirs\n", CancellationToken.None);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));

        Assert.Contains("Unresolved merge conflict", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedUnknownComponentFile_IsNotLoadedSoItCannotBePushed()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        WorkspaceSynchronizer.WriteCloudCache(accessor, CloudDefinition());
        Write(accessor, "topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Empty(read.Components);
    }
}
