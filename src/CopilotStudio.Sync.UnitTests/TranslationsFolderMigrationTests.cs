// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class TranslationsFolderMigrationTests
{
    private const string Bot = "cr834_iqagent";
    private const string CapabilitySchema = Bot + ".iqCapability.mail_x1Y";
    private const string CliPath = "capabilities/tools/" + CapabilitySchema + ".mcs.yml";
    private const string ClassicPath = "tools/" + CapabilitySchema + ".mcs.yml";
    private const string LegacyPath = "translations/" + CapabilitySchema + ".mcs.yml";

    [Theory]
    [InlineData(true, CliPath)]
    [InlineData(false, ClassicPath)]
    public async Task GetLocalChanges_CapabilityInLegacyTranslationsFolder_ReportsNoChanges(bool isCliAgent, string projectedPath)
    {
        var context = await CloneLegacyWorkspaceAsync(isCliAgent, projectedPath);

        var (definition, changes) = await GetLocalChangesAsync(context);

        Assert.Contains(definition.Components, component => component.SchemaNameString == CapabilitySchema);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task GetLocalChanges_CapabilityEditedInLegacyTranslationsFolder_ReportsUpdateAtLegacyPath()
    {
        var context = await CloneLegacyWorkspaceAsync(isCliAgent: true, CliPath);
        WriteFile(context, LegacyPath, ReadFile(context, LegacyPath).Replace("capability: mail", "capability: calendar", StringComparison.Ordinal));

        var (_, changes) = await GetLocalChangesAsync(context);

        var change = Assert.Single(changes);
        Assert.Equal((ChangeType.Update, CapabilitySchema, LegacyPath), (change.ChangeType, change.SchemaName, change.Uri));
    }

    [Fact]
    public async Task Pull_NoRemoteChanges_MovesCapabilityOutOfLegacyTranslationsFolder()
    {
        var context = await CloneLegacyWorkspaceAsync(isCliAgent: true, CliPath);
        var legacyContent = ReadFile(context, LegacyPath);

        await PullAsync(context);

        Assert.False(context.Accessor.Exists(new AgentFilePath(LegacyPath)));
        Assert.Equal(legacyContent, ReadFile(context, CliPath));
        Assert.Contains($"Moved '{LegacyPath}' to '{CliPath}' to match the current workspace layout.", context.Messages);
        Assert.Empty((await GetLocalChangesAsync(context)).Changes);
    }

    [Fact]
    public async Task Pull_RemoteUpdate_WritesCapabilityToProjectedPathAndRemovesLegacyFile()
    {
        var context = await CloneLegacyWorkspaceAsync(isCliAgent: true, CliPath);

        await PullAsync(context, new BotComponentUpdate(CreateCapability(context.CapabilityId, version: 2, confirmWrite: true)));

        Assert.False(context.Accessor.Exists(new AgentFilePath(LegacyPath)));
        Assert.Contains("confirmWrite: true", ReadFile(context, CliPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_RemoteDelete_RemovesCapabilityFromLegacyTranslationsFolder()
    {
        var context = await CloneLegacyWorkspaceAsync(isCliAgent: true, CliPath);

        await PullAsync(context, new BotComponentDelete(context.CapabilityId, 1));

        Assert.False(context.Accessor.Exists(new AgentFilePath(LegacyPath)));
        Assert.False(context.Accessor.Exists(new AgentFilePath(CliPath)));
    }

    [Fact]
    public async Task Pull_ProjectedFileAlreadyExists_KeepsBothFiles()
    {
        var context = await CloneLegacyWorkspaceAsync(isCliAgent: true, CliPath);
        var legacyContent = ReadFile(context, LegacyPath);
        var projectedContent = legacyContent.Replace("capability: mail", "capability: calendar", StringComparison.Ordinal);
        WriteFile(context, CliPath, projectedContent);

        await PullAsync(context);

        Assert.Equal(projectedContent, ReadFile(context, CliPath));
        Assert.Equal(legacyContent, ReadFile(context, LegacyPath));
    }

    [Theory]
    [InlineData("agents/Child/tools/" + CapabilitySchema + ".mcs.yml", "agents/Child/translations/" + CapabilitySchema + ".mcs.yml")]
    [InlineData(LegacyPath, null)]
    public void TryGetLegacyPath_MapsProjectedPathToTranslationsFolder(string componentPath, string? expectedLegacyPath)
    {
        var found = TranslationsFolderMigration.TryGetLegacyPath(new AgentFilePath(componentPath), CreateCapability(Guid.NewGuid(), version: 1), Bot, out var legacyPath);

        Assert.Equal(expectedLegacyPath, found ? legacyPath.ToString() : null);
    }

    private sealed record MigrationContext(
        WorkspaceSynchronizer Sync,
        InMemoryFileAccessor Accessor,
        DirectoryPath Workspace,
        Mock<IIslandControlPlaneService> Island,
        Mock<ISyncDataverseClient> Dataverse,
        AuthoringOperationContext OperationContext,
        AgentSyncInfo SyncInfo,
        BotEntity BotEntity,
        Guid CapabilityId,
        List<string> Messages);

    private static async Task<MigrationContext> CloneLegacyWorkspaceAsync(bool isCliAgent, string projectedPath)
    {
        var messages = new List<string>();
        var (synchronizer, factory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure(new TestSyncProgress(messages));
        var workspace = new DirectoryPath($"c:/test/translations-migration-{Guid.NewGuid():N}/");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowMetadata>());
        dataverse.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());

        var context = new MigrationContext(
            synchronizer,
            (InMemoryFileAccessor)factory.Create(workspace),
            workspace,
            island,
            dataverse,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}\n" + (isCliAgent ? "template: cliagent-1.0.0\n" : string.Empty))!,
            Guid.NewGuid(),
            messages);

        SetupChangeset(context, new BotComponentInsert(CreateCapability(context.CapabilityId, version: 1)));
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), context.OperationContext, dataverse.Object, context.SyncInfo, CancellationToken.None);
        context.Accessor.Replace(new AgentFilePath(projectedPath), new AgentFilePath(LegacyPath));

        return context;
    }

    private static async Task<(DefinitionBase Definition, ImmutableArray<Change> Changes)> GetLocalChangesAsync(MigrationContext context)
    {
        var definition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);
        var (_, changes) = await context.Sync.GetLocalChangesAsync(context.Workspace, definition, CancellationToken.None);
        return (definition, changes);
    }

    private static async Task PullAsync(MigrationContext context, params BotComponentChange[] changes)
    {
        var localDefinition = await context.Sync.ReadWorkspaceDefinitionAsync(context.Workspace, CancellationToken.None);
        SetupChangeset(context, changes);
        await context.Sync.PullExistingChangesAsync(context.Workspace, context.OperationContext, localDefinition, context.Dataverse.Object, context.SyncInfo, CancellationToken.None);
    }

    private static void SetupChangeset(MigrationContext context, params BotComponentChange[] changes)
        => context.Island
            .Setup(island => island.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(changes, context.BotEntity, Guid.NewGuid().ToString("N")));

    private static DialogComponent CreateCapability(Guid id, long version, bool? confirmWrite = null)
    {
        var builder = new DialogComponent(
            schemaName: CapabilitySchema,
            displayName: "Mail",
            description: "Reads mail",
            id: id,
            parentBotComponentId: default,
            dialog: new IQCapability.Builder { Capability = "mail", ConfirmWrite = confirmWrite }.Build()).ToBuilder();
        builder.Version = version;
        return (DialogComponent)builder.Build();
    }

    private static string ReadFile(MigrationContext context, string path)
    {
        using var stream = context.Accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteFile(MigrationContext context, string path, string content)
    {
        using var stream = context.Accessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
