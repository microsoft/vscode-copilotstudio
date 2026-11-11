// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.EnvironmentVariableTestFixtures;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Tests for environment variable file projection — write env vars as
/// environmentvariables/*.mcs.yml during clone, and read them back.
/// </summary>
public class EnvironmentVariableProjectionTests
{
    [Fact]
    public void GetEnvironmentVariablePath_UsesSchemaNameAsFileName()
    {
        var envVar = CreateEnvVar("cr123.myVariable");
        var path = WorkspaceSynchronizer.GetEnvironmentVariablePath(envVar);
        Assert.Equal("environmentvariables/cr123.myVariable.mcs.yml", path.ToString());
    }

    [Fact]
    public async Task Clone_WritesEnvironmentVariableFiles()
    {
        // Arrange: clone returns a changeset with env var inserts
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var envVar1 = CreateEnvVar("cr123.apiEndpoint", "API Endpoint");
        var envVar2 = CreateEnvVar("cr123.maxRetries", "Max Retries");
        var botEntity = (CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123") ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));

        // Build changeset with env var changes using the full constructor
        var envVarChanges = new EnvironmentVariableChange[]
        {
            new EnvironmentVariableInsert(envVar1),
            new EnvironmentVariableInsert(envVar2),
        };

        var changeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: envVarChanges,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: null,
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: botEntity,
            changeToken: "token-1");

        mockIsland
            .Setup(x => x.GetComponentsAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        var workspace = new DirectoryPath("c:/test/agent/");
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());

        // Act
        var referenceTracker = new ReferenceTracker();
        await synchronizer.CloneChangesAsync(
            workspace,
            referenceTracker,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None);

        // Assert: env var files exist
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.True(fileAccessor.Exists(new AgentFilePath("environmentvariables/cr123.apiEndpoint.mcs.yml")),
            "First env var file should be projected");
        Assert.True(fileAccessor.Exists(new AgentFilePath("environmentvariables/cr123.maxRetries.mcs.yml")),
            "Second env var file should be projected");
    }

    [Fact]
    public async Task Clone_WithNoEnvVars_NoEnvVarFilesCreated()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var botEntity = (CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot") ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        var changeset = new PvaComponentChangeSet(null, botEntity, "token-1");

        mockIsland
            .Setup(x => x.GetComponentsAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        var workspace = new DirectoryPath("c:/test/agent/");
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());

        var referenceTracker = new ReferenceTracker();
        await synchronizer.CloneChangesAsync(
            workspace,
            referenceTracker,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None);

        // Assert: no env var files
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var envVarFiles = fileAccessor.Files.Keys.Where(k => k.StartsWith("environmentvariables/")).ToList();
        Assert.Empty(envVarFiles);
    }

    [Fact]
    public async Task Clone_ProjectsReferencedSharedEnvironmentVariables_ButNotUnreferencedOrManagedOnes()
    {
        var (dataverse, lookups) = CreateDataverse(TargetEnvironmentVariable("cr9f2_managed", isManaged: true));

        var (_, fileAccessor, _, _) = await CloneAsync(
            dataverse,
            new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_managed}") },
            CreateEnvironmentVariable(OwnedSchemaName),
            CreateEnvironmentVariable(SharedSchemaName),
            CreateEnvironmentVariable("cr9f2_managed"),
            CreateEnvironmentVariable("cr9f2_unreferenced"));

        Assert.True(fileAccessor.Exists(GetPath(OwnedSchemaName)));
        Assert.True(fileAccessor.Exists(SharedPath));
        Assert.False(fileAccessor.Exists(GetPath("cr9f2_managed")));
        Assert.False(fileAccessor.Exists(GetPath("cr9f2_unreferenced")));
        Assert.Equal(new[] { "cr9f2_managed", SharedSchemaName }, Assert.Single(lookups).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Clone_ProjectedSharedEnvironmentVariable_RoundTripsTheCloudDefinition()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName, value: "current");
        var (dataverse, _) = CreateDataverse();

        var (_, fileAccessor, _, _) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);

        Assert.True(shared.Equals(ReadEnvironmentVariableFile(fileAccessor, SharedPath), NodeComparison.Structural));
        Assert.True(shared.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables, environmentVariable => environmentVariable.SchemaName.Value == SharedSchemaName), NodeComparison.Structural));
    }

    [Fact]
    public async Task Pull_WhenLocalTopicStartsReferencingSharedEnvironmentVariable_ProjectsIt()
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) }, CreateEnvironmentVariable(SharedSchemaName));
        Assert.False(fileAccessor.Exists(SharedPath));
        ReplaceText(fileAccessor, FindTopicPath(fileAccessor), "Topic.unused", "Env.cr9f2_shared");

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2"));

        Assert.True(fileAccessor.Exists(SharedPath));
    }

    [Fact]
    public async Task Pull_WhenNoComponentReferencesSharedEnvironmentVariable_RemovesItsFile()
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, CreateEnvironmentVariable(SharedSchemaName));
        Assert.True(fileAccessor.Exists(SharedPath));
        ReplaceText(fileAccessor, FindTopicPath(fileAccessor), "Env.cr9f2_shared", "Topic.unused");

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2"));

        Assert.False(fileAccessor.Exists(SharedPath));
    }

    [Fact]
    public async Task Pull_WhenCloudChangesProjectedSharedEnvironmentVariable_OverwritesItsFile()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var (dataverse, lookups) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        lookups.Clear();

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(shared.WithDefaultValue("two")) }));

        Assert.Equal("two", ReadEnvironmentVariableFile(fileAccessor, SharedPath).DefaultValue);
        Assert.Empty(lookups);
    }

    [Theory]
    [InlineData(OwnedSchemaName, UnreferencedActivity)]
    [InlineData(SharedSchemaName, SharedActivity)]
    public async Task Pull_WhenCloudDeletesEnvironmentVariable_RemovesItsFile(string schemaName, string activity)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        Assert.True(fileAccessor.Exists(GetPath(schemaName)));

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableDelete(environmentVariable.Id, (environmentVariable.ValueComponent ?? throw new Xunit.Sdk.XunitException("Expected a value.")).Id, environmentVariable.Version) }));

        Assert.False(fileAccessor.Exists(GetPath(schemaName)));
    }

    [Fact]
    public async Task RetargetSync_KeepsProjectedSharedEnvironmentVariableThatTargetComponentsDoNotReference()
    {
        var (synchronizer, fileAccessorFactory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/environment-variables-{Guid.NewGuid():N}/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"));
        var original = fileAccessor.Files[SharedPath.ToString()];
        SetupRemoteChanges(island, CreateChangeSet("token-1", environmentVariableChanges: new[] { new EnvironmentVariableInsert(CreateEnvironmentVariable(SharedSchemaName, defaultValue: "target")) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse();

        await synchronizer.SyncWorkspaceAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeToken: null, updateWorkspaceDirectory: true, dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, cloudFlowMetadata: null, CancellationToken.None);

        Assert.Equal(original, fileAccessor.Files[SharedPath.ToString()]);
    }

    [Fact]
    public async Task GetRemoteChanges_WhenCloudChangesProjectedSharedEnvironmentVariable_ReportsUpdateWithoutWritingCache()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        var cache = fileAccessor.Files[CachePath.ToString()];
        SetupRemoteChanges(island, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(shared.WithDefaultValue("two")) }));

        var (_, changes) = await synchronizer.GetRemoteChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var change = Assert.Single(changes, change => change.SchemaName == SharedSchemaName);
        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal(SharedPath.ToString(), change.Uri);
        Assert.Equal(cache, fileAccessor.Files[CachePath.ToString()]);
    }

    private static EnvironmentVariableDefinition CreateEnvVar(string schemaName, string? displayName = null)
    {
        var builder = new EnvironmentVariableDefinition().ToBuilder();
        builder.SchemaName = new EnvironmentVariableDefinitionSchemaName(schemaName);
        builder.DisplayName = displayName ?? $"Test {schemaName}";
        builder.Id = new EnvironmentVariableDefinitionId(Guid.NewGuid());
        return builder.Build();
    }
}
