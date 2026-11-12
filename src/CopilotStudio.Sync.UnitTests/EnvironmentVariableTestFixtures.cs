// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal static class EnvironmentVariableTestFixtures
{
    internal const string AgentSchemaName = "cr123";
    internal const string OwnedSchemaName = "cr123.owned";
    internal const string SharedSchemaName = "cr9f2_shared";
    internal const string SharedActivity = "{Env.cr9f2_shared}";
    internal const string UnreferencedActivity = "{Topic.unused}";

    internal static readonly AgentFilePath SharedPath = GetPath(SharedSchemaName);
    internal static readonly AgentFilePath CachePath = new(".mcs/botdefinition.json");
    internal static readonly AgentFilePath ChangeTokenPath = new(".mcs/changetoken.txt");
    internal static readonly AgentFilePath RetargetUploadedEnvironmentVariablesPath = new(".mcs/.retarget-uploaded-environmentvariables.json");

    internal static AgentFilePath GetPath(string schemaName) => new($"environmentvariables/{schemaName}.mcs.yml");

    internal static BotEntity CreateBotEntity() => new BotEntity.Builder { SchemaName = new BotEntitySchemaName(AgentSchemaName) }.Build();

    internal static EnvironmentVariableDefinition CreateEnvironmentVariable(string schemaName, string defaultValue = "one", EnvironmentVariableType type = EnvironmentVariableType.String, string? value = null)
    {
        var id = new EnvironmentVariableDefinitionId(Guid.NewGuid());
        return new EnvironmentVariableDefinition(
            displayName: schemaName,
            schemaName: schemaName,
            id: id,
            defaultValue: defaultValue,
            valueComponent: value is null ? null : new EnvironmentVariableValue(id: Guid.NewGuid(), definitionId: id, value: value),
            type: type);
    }

    internal static DialogComponent CreateTopic(string name, string activity)
    {
        var dialog = CodeSerializer.Deserialize<BotElement>(
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnRecognizedIntent\n" +
            "  id: main\n" +
            "  intent: {}\n" +
            "  actions:\n" +
            "    - kind: SendActivity\n" +
            "      id: sendMessage\n" +
            $"      activity: \"{activity}\"\n") as DialogBase;
        return new DialogComponent(
            schemaName: $"{AgentSchemaName}.topic.{name}",
            displayName: name,
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: dialog ?? throw new Xunit.Sdk.XunitException("Expected a dialog."));
    }

    internal static PvaComponentChangeSet CreateChangeSet(string changeToken, IEnumerable<BotComponentChange>? componentChanges = null, IEnumerable<EnvironmentVariableChange>? environmentVariableChanges = null, BotEntity? bot = null) => new(
        botComponentChanges: componentChanges,
        connectorDefinitionChanges: null,
        environmentVariableChanges: environmentVariableChanges,
        connectionReferenceChanges: null,
        aIPluginOperationChanges: null,
        componentCollectionChanges: null,
        dataverseTableSearchChanges: null,
        dataverseTableSearchEntityConfigurationChanges: null,
        connectedAgentDefinitionChanges: null,
        bot: bot,
        changeToken: changeToken);

    internal static EnvironmentVariableDelete CreateDelete(EnvironmentVariableDefinition environmentVariable) => new(environmentVariable.Id, environmentVariable.ValueComponent?.Id ?? default, environmentVariable.Version);

    internal static EnvironmentVariableDefinitionInfo TargetEnvironmentVariable(string schemaName, bool isManaged = false) => new() { DefinitionId = Guid.NewGuid(), SchemaName = schemaName, IsManaged = isManaged };

    internal static (Mock<ISyncDataverseClient> Dataverse, List<string[]> Lookups) CreateDataverse(params EnvironmentVariableDefinitionInfo[] targetEnvironmentVariables) => CreateDataverse(onLookup: null, targetEnvironmentVariables);

    internal static (Mock<ISyncDataverseClient> Dataverse, List<string[]> Lookups) CreateDataverse(Action? onLookup, params EnvironmentVariableDefinitionInfo[] targetEnvironmentVariables)
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        var lookups = new List<string[]>();
        dataverse.As<IEnvironmentVariableOperations>()
            .Setup(operations => operations.GetEnvironmentVariableDefinitionsBySchemaNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> schemaNames, CancellationToken _) =>
            {
                var requested = schemaNames.ToArray();
                lookups.Add(requested);
                onLookup?.Invoke();
                return targetEnvironmentVariables.Where(info => requested.Contains(info.SchemaName, StringComparer.OrdinalIgnoreCase)).ToArray();
            });
        return (dataverse, lookups);
    }

    internal static async Task<(WorkspaceSynchronizer Synchronizer, InMemoryFileAccessor FileAccessor, Mock<IIslandControlPlaneService> Island, DirectoryPath Workspace)> CloneAsync(Mock<ISyncDataverseClient> dataverse, IEnumerable<BotComponentBase> components, params EnvironmentVariableDefinition[] environmentVariables)
    {
        var (synchronizer, fileAccessor, island, workspace) = CreateWorkspace();
        SetupRemoteChanges(island, CreateChangeSet("token-1", components.Select(component => new BotComponentInsert(component)), environmentVariables.Select(environmentVariable => new EnvironmentVariableInsert(environmentVariable)), CreateBotEntity()));
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), ComponentWriterDefensiveTests.CreateMockOperationContext(), dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);
        return (synchronizer, fileAccessor, island, workspace);
    }

    internal static (WorkspaceSynchronizer Synchronizer, InMemoryFileAccessor FileAccessor, Mock<IIslandControlPlaneService> Island, DirectoryPath Workspace) CreateWorkspace(params EnvironmentVariableDefinition[] environmentVariables)
    {
        var (synchronizer, fileAccessorFactory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/environment-variables-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        foreach (var environmentVariable in environmentVariables)
        {
            WriteEnvironmentVariableFile(fileAccessor, environmentVariable);
        }

        return (synchronizer, fileAccessor, island, workspace);
    }

    internal static Task<WorkspaceSyncInfo> RetargetSyncAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace, Mock<ISyncDataverseClient> dataverse, bool updateWorkspaceDirectory = true) => synchronizer.SyncWorkspaceAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeToken: null, updateWorkspaceDirectory, dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, cloudFlowMetadata: null, CancellationToken.None);

    internal static void SetupRemoteChanges(Mock<IIslandControlPlaneService> island, PvaComponentChangeSet changeSet) => island
        .Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(changeSet);

    internal static List<PvaComponentChangeSet> CaptureSaves(Mock<IIslandControlPlaneService> island, Func<PvaComponentChangeSet, PvaComponentChangeSet>? confirm = null)
    {
        var saves = new List<PvaComponentChangeSet>();
        island
            .Setup(service => service.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuthoringOperationContextBase _, PvaComponentChangeSet changeSet, CancellationToken _) =>
            {
                saves.Add(changeSet);
                return confirm?.Invoke(changeSet) ?? CreateChangeSet("token-2", ConfirmComponentChanges(changeSet));
            });
        return saves;
    }

    internal static IEnumerable<BotComponentChange> ConfirmComponentChanges(PvaComponentChangeSet changeSet) => changeSet.BotComponentChanges.Select(change => change is BotComponentInsert { Component: { } component } ? new BotComponentInsert(component.WithId(Guid.NewGuid())) : change);

    internal static async Task<DefinitionBase> PullAsync(WorkspaceSynchronizer synchronizer, Mock<IIslandControlPlaneService> island, DirectoryPath workspace, Mock<ISyncDataverseClient> dataverse, PvaComponentChangeSet remoteChangeSet, ICollection<WorkspaceDiagnostic>? conflicts = null, DefinitionBase? localDefinition = null)
    {
        SetupRemoteChanges(island, remoteChangeSet);
        return await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), localDefinition ?? await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None, conflicts: conflicts);
    }

    internal static void ClearCloudCache(IFileAccessor fileAccessor) => WorkspaceSynchronizer.WriteCloudCache(fileAccessor, ReadCache(fileAccessor).WithComponents(Array.Empty<BotComponentBase>()).WithEnvironmentVariables(Array.Empty<EnvironmentVariableDefinition>()));

    internal static DefinitionBase ReadCache(IFileAccessor fileAccessor) => WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor) ?? throw new Xunit.Sdk.XunitException("Expected a cloud cache.");

    internal static AgentFilePath FindTopicPath(InMemoryFileAccessor fileAccessor) => new(Assert.Single(fileAccessor.Files.Keys, key => key.StartsWith("topics/", StringComparison.Ordinal)));

    internal static string ReadText(InMemoryFileAccessor fileAccessor, AgentFilePath path) => Encoding.UTF8.GetString(fileAccessor.Files[path.ToString()]);

    internal static void WriteText(IFileAccessor fileAccessor, AgentFilePath path, string text)
    {
        using var stream = fileAccessor.OpenWrite(path);
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static void ReplaceText(InMemoryFileAccessor fileAccessor, AgentFilePath path, string oldValue, string newValue)
    {
        var text = ReadText(fileAccessor, path);
        Assert.Contains(oldValue, text, StringComparison.Ordinal);
        WriteText(fileAccessor, path, text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    internal static void WriteEnvironmentVariableFile(IFileAccessor fileAccessor, EnvironmentVariableDefinition environmentVariable)
    {
        using var writer = new StringWriter();
        CodeSerializer.Serialize(writer, environmentVariable);
        WriteText(fileAccessor, GetPath(environmentVariable.SchemaName.Value), writer.ToString());
    }

    internal static EnvironmentVariableDefinition ReadEnvironmentVariableFile(InMemoryFileAccessor fileAccessor, AgentFilePath path) => CodeSerializer.Deserialize<EnvironmentVariableDefinition>(ReadText(fileAccessor, path)) ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
}
