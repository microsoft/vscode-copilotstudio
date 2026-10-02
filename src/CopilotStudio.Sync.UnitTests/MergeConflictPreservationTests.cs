// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class MergeConflictPreservationTests
{
    private const string Bot = "cr834_n2a8_PwdEI5";
    private const string ToolSchema = "cr834_n2a8_PwdEI5.tool.connected-agent.cr834_n2a8_PwdEI5.action.crf9a_nagentn1_T2U1EY_iL5CJBUv";
    private const string ToolPath = "capabilities/tools/action.crf9a_nagentn1_T2U1EY_iL5CJBUv.mcs.yml";
    private const string SkillSchema = "cr834_n2a8_PwdEI5.skill.get-us-weather";
    private const string BaseDescription = "base description";
    private const string BaseDisplayName = "NAgent N1";

    [Fact]
    public async Task Pull_RemoteDeleteWithLocalEdit_KeepsTheConflictedFile()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentDelete(context.ToolId, 1));
        await PullAsync(context, localDefinition);

        var fileContent = ReadFile(context, ToolPath);
        Assert.Contains("crf9a_LOCAL_EDIT", fileContent, StringComparison.Ordinal);
        Assert.Contains("<<<<<<<", fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_RemoteDeleteWithLocalEdit_KeepsTheComponentInThePullResult()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentDelete(context.ToolId, 1));
        var pulled = await PullAsync(context, localDefinition);

        Assert.Contains(pulled.Components, component => component.SchemaNameString == ToolSchema);
    }

    [Fact]
    public async Task Pull_RemoteDeleteWithLocalEdit_LeavesTheDeleteInTheCloudCacheOnly()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentDelete(context.ToolId, 1));
        await PullAsync(context, localDefinition);

        Assert.DoesNotContain(ReadCache(context).Components, component => component.SchemaNameString == ToolSchema);
        Assert.DoesNotContain("<<<<<<<", ReadFile(context, ".mcs/botdefinition.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_RemoteDeleteWithoutLocalEdit_StillDeletesTheFile()
    {
        var context = await CloneAsync();
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentDelete(context.ToolId, 1));
        await PullAsync(context, localDefinition);

        Assert.False(context.Accessor.Exists(new AgentFilePath(ToolPath)));
    }

    [Fact]
    public void ApplyThreeWayMerge_RemoteDeleteWithLocalSkillContentEdit_DropsTheRemoteDelete()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var skillId = Guid.NewGuid();
        var snapshot = new BotDefinition().WithComponents(new BotComponentBase[] { CreateSkill(skillId, "base instructions") });

        var merged = sync.ApplyThreeWayMerge(
            SkillChanges(new BotComponentUpdate(CreateSkill(skillId, "local instructions"))),
            SkillChanges(new BotComponentDelete(skillId, 1)),
            snapshot);

        Assert.DoesNotContain(merged.BotComponentChanges, change => change is BotComponentDelete);
    }

    [Fact]
    public void ApplyThreeWayMerge_RemoteDeleteWithLocalSkillContentEdit_KeepsTheLocalContent()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var skillId = Guid.NewGuid();
        var snapshot = new BotDefinition().WithComponents(new BotComponentBase[] { CreateSkill(skillId, "base instructions") });

        var merged = sync.ApplyThreeWayMerge(
            SkillChanges(new BotComponentUpdate(CreateSkill(skillId, "local instructions"))),
            SkillChanges(new BotComponentDelete(skillId, 1)),
            snapshot);

        var kept = merged.BotComponentChanges.OfType<BotComponentUpsert>().Single(change => change.Component?.SchemaNameString == SkillSchema);
        Assert.Contains("local instructions", ((InlineAgentSkill)((DialogComponent)kept.Component!).Dialog!).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyThreeWayMerge_RemoteDeleteWithoutLocalEdit_KeepsTheRemoteDelete()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var skillId = Guid.NewGuid();
        var snapshot = new BotDefinition().WithComponents(new BotComponentBase[] { CreateSkill(skillId, "base instructions") });

        var merged = sync.ApplyThreeWayMerge(
            (new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), null, "token"), ImmutableArray<Change>.Empty),
            SkillChanges(new BotComponentDelete(skillId, 1)),
            snapshot);

        Assert.Contains(merged.BotComponentChanges, change => change is BotComponentDelete);
    }

    [Fact]
    public async Task Pull_BodyConflict_ReportsTheConflictedFile()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_REMOTE_EDIT", version: 2)));
        var conflicts = new List<WorkspaceDiagnostic>();
        await PullAsync(context, localDefinition, conflicts);

        var diagnostic = Assert.Single(conflicts);
        Assert.Equal(ToolPath, diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pull_DescriptionConflict_ReportsTheFileAndPreservesTheMergedBody(bool remoteBodyChanged)
    {
        var context = await CloneAsync();
        EditDescription(context, "local description");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);
        var remoteBody = remoteBodyChanged ? "crf9a_REMOTE_EDIT" : "crf9a_nagentn1_T2U1EY";
        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, "remote description", remoteBody, version: 2)));
        var conflicts = new List<WorkspaceDiagnostic>();

        var pulled = await PullAsync(context, localDefinition, conflicts);

        var diagnostic = Assert.Single(conflicts);
        Assert.Equal(ToolPath, diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        var text = ReadFile(context, ToolPath);
        Assert.Equal(McsConflictMarkers.FindFirstMarkerLine(text), diagnostic.Line);
        Assert.True(diagnostic.Line > 0);
        Assert.Equal(1, diagnostic.Column);
        Assert.Contains("local description", text, StringComparison.Ordinal);
        Assert.Contains("remote description", text, StringComparison.Ordinal);
        Assert.Contains(remoteBody, text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n').Where(line => line.TrimEnd('\r') == "mcs.metadata:"));
        var component = Assert.Single(pulled.Components.Where(component => component.SchemaNameString == ToolSchema));
        Assert.Contains(remoteBody, CodeSerializer.Serialize(Assert.IsAssignableFrom<DialogBase>(component.RootElement)), StringComparison.Ordinal);
        var cached = Assert.Single(ReadCache(context).Components.Where(component => component.SchemaNameString == ToolSchema));
        Assert.Equal("remote description", cached.Description);
        Assert.DoesNotContain("local description", ReadFile(context, ".mcs/botdefinition.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("<<<<<<<", ReadFile(context, ".mcs/botdefinition.json"), StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None));
        Assert.Equal(ToolPath, Assert.Single(failure.Diagnostics).FilePath);
    }

    [Fact]
    public async Task Pull_SettingsConflict_ReportsTheSettingsFile()
    {
        var context = await CloneAsync();
        EditSettingsDisplayName(context, "LOCAL display name");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupSettingsChangeset(context, "REMOTE display name");
        var conflicts = new List<WorkspaceDiagnostic>();
        await PullAsync(context, localDefinition, conflicts);

        Assert.Contains(conflicts, diagnostic => diagnostic.FilePath == "settings.mcs.yml" && diagnostic.Kind == WorkspaceDiagnosticKind.MergeConflict);
    }

    [Fact]
    public async Task Pull_WithoutConflicts_ReportsNothing()
    {
        var context = await CloneAsync();
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_REMOTE_EDIT", version: 2)));
        var conflicts = new List<WorkspaceDiagnostic>();
        await PullAsync(context, localDefinition, conflicts);

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task Pull_RepeatedSkillComponents_ReportTheConflictedManifestOnce()
    {
        var context = await CloneAsync();
        var manifestText = McsConflictMarkers.Build("local instructions\n", "remote instructions\n");
        var skill = CreateSkill(Guid.NewGuid(), manifestText);
        var cached = ReadCache(context);
        var localDefinition = cached.WithComponents(cached.Components.Concat(new[] { skill, skill }));
        const string manifestPath = "behaviors/get-us-weather/SKILL.md";
        WriteFile(context, manifestPath, manifestText);
        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_nagentn1_T2U1EY", version: 1)));
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(context, localDefinition, conflicts);

        var diagnostic = Assert.Single(conflicts);
        Assert.Equal(manifestPath, diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        Assert.Equal(manifestText, ReadFile(context, manifestPath));
        Assert.DoesNotContain("<<<<<<<", ReadFile(context, ".mcs/botdefinition.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_BodyConflict_KeepsALocalOnlyDescription()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        EditDescription(context, "local description");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_REMOTE_EDIT", version: 2)));
        await PullAsync(context, localDefinition);

        var fileContent = ReadFile(context, ToolPath);
        Assert.Contains("<<<<<<<", fileContent, StringComparison.Ordinal);
        Assert.Contains("local description", fileContent, StringComparison.Ordinal);
        Assert.DoesNotContain(BaseDescription, fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_BodyConflict_MarksADescriptionChangedOnBothSides()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        EditDescription(context, "local description");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, "remote description", "crf9a_REMOTE_EDIT", version: 2)));
        await PullAsync(context, localDefinition);

        var fileContent = ReadFile(context, ToolPath);
        Assert.Contains("local description", fileContent, StringComparison.Ordinal);
        Assert.Contains("remote description", fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_BodyConflict_KeepsTheLocalDisplayNameSoThePathIsStable()
    {
        var context = await CloneAsync();
        EditBody(context, "crf9a_LOCAL_EDIT");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);

        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_REMOTE_EDIT", version: 2, displayName: "Renamed Remotely")));
        await PullAsync(context, localDefinition);

        var fileContent = ReadFile(context, ToolPath);
        Assert.Contains("<<<<<<<", fileContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Renamed Remotely", fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_DisplayNameConflict_ReportsTheFileAndKeepsTheLocalName()
    {
        var context = await CloneAsync();
        EditDisplayName(context, "Renamed Locally");
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);
        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_nagentn1_T2U1EY", version: 2, displayName: "Renamed Remotely")));
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(context, localDefinition, conflicts);

        var diagnostic = Assert.Single(conflicts);
        Assert.Equal(ToolPath, diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        var text = ReadFile(context, ToolPath);
        Assert.Contains("Renamed Locally", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Renamed Remotely", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_DisplayNameChangedOnOneSideOnly_ReportsNothing()
    {
        var context = await CloneAsync();
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);
        SetupChangeset(context, new BotComponentUpdate(CreateTool(context.ToolId, BaseDescription, "crf9a_nagentn1_T2U1EY", version: 2, displayName: "Renamed Remotely")));
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(context, localDefinition, conflicts);

        Assert.Empty(conflicts);
    }

    private static void EditDisplayName(PullContext context, string displayName)
        => WriteFile(context, ToolPath, ReadFile(context, ToolPath).Replace(BaseDisplayName, displayName));

    private sealed record PullContext(
        WorkspaceSynchronizer Sync,
        InMemoryFileAccessor Accessor,
        DirectoryPath Workspace,
        Mock<IIslandControlPlaneService> Island,
        Mock<ISyncDataverseClient> Dataverse,
        AuthoringOperationContext OperationContext,
        AgentSyncInfo SyncInfo,
        Guid ToolId);

    private static async Task<PullContext> CloneAsync()
    {
        var (synchronizer, factory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/merge-conflict-{Guid.NewGuid():N}/");
        var toolId = Guid.NewGuid();

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowMetadata>());
        dataverse.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());

        var context = new PullContext(
            synchronizer,
            (InMemoryFileAccessor)factory.Create(workspace),
            workspace,
            island,
            dataverse,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            toolId);

        SetupChangeset(context, new BotComponentInsert(CreateTool(toolId, BaseDescription, "crf9a_nagentn1_T2U1EY", version: 1)));
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), context.OperationContext, dataverse.Object, context.SyncInfo, CancellationToken.None);

        return context;
    }

    private static Task<DefinitionBase> PullAsync(PullContext context, DefinitionBase localDefinition, ICollection<WorkspaceDiagnostic>? conflicts = null)
        => context.Sync.PullExistingChangesAsync(context.Workspace, context.OperationContext, localDefinition, context.Dataverse.Object, context.SyncInfo, CancellationToken.None, downloadAllKnowledgeFiles: false, conflicts);

    private static void SetupChangeset(PullContext context, BotComponentChange change)
        => context.Island
            .Setup(island => island.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new[] { change }, CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}\ntemplate: cliagent-1.0.0\n")!, Guid.NewGuid().ToString("N")));

    private static void SetupSettingsChangeset(PullContext context, string displayName)
    {
        var builder = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}\ndisplayName: {displayName}\ntemplate: cliagent-1.0.0\n")!.ToBuilder();
        builder.Version = 2;
        context.Island
            .Setup(island => island.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), builder.Build(), Guid.NewGuid().ToString("N")));
    }

    private static void EditSettingsDisplayName(PullContext context, string displayName)
    {
        var settings = ReadFile(context, "settings.mcs.yml").Replace("\r\n", "\n");
        WriteFile(context, "settings.mcs.yml", settings.Contains("displayName:", StringComparison.Ordinal)
            ? string.Join("\n", settings.Split('\n').Select(line => line.StartsWith("displayName:", StringComparison.Ordinal) ? $"displayName: {displayName}" : line))
            : $"displayName: {displayName}\n" + settings);
    }

    private static DialogComponent CreateTool(Guid id, string description, string botSchemaName, long version, string displayName = "NAgent N1")
    {
        var dialog = CodeSerializer.Deserialize<BotElement>(
            $"kind: ConnectedAgentTool\nhistoryType:\n  kind: ConversationHistory\nbotSchemaName: {botSchemaName}\n") as DialogBase;
        var builder = new DialogComponent(
            schemaName: ToolSchema,
            displayName: displayName,
            description: description,
            id: id,
            parentBotComponentId: default,
            dialog: dialog!).ToBuilder();
        builder.Version = version;
        return (DialogComponent)builder.Build();
    }

    private static DialogComponent CreateSkill(Guid id, string content)
    {
        var dialog = new InlineAgentSkill.Builder { Content = content }.Build();
        return new DialogComponent(
            schemaName: SkillSchema,
            displayName: "get-us-weather",
            description: BaseDescription,
            id: id,
            parentBotComponentId: default,
            dialog: dialog);
    }

    private static (PvaComponentChangeSet, ImmutableArray<Change>) SkillChanges(BotComponentChange change)
        => (new PvaComponentChangeSet(new[] { change }, null, "token"), ImmutableArray.Create(new Change { SchemaName = SkillSchema, ChangeKind = nameof(DialogComponent) }));

    private static void EditBody(PullContext context, string botSchemaName)
        => WriteFile(context, ToolPath, ReadFile(context, ToolPath).Replace("crf9a_nagentn1_T2U1EY", botSchemaName));

    private static void EditDescription(PullContext context, string description)
        => WriteFile(context, ToolPath, ReadFile(context, ToolPath).Replace(BaseDescription, description));

    private static string ReadFile(PullContext context, string path)
    {
        using var stream = context.Accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteFile(PullContext context, string path, string content)
    {
        using var stream = context.Accessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static DefinitionBase ReadCache(PullContext context)
    {
        using var stream = context.Accessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }
}
