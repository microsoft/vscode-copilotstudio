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

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { CreateDelete(environmentVariable) }));

        Assert.False(fileAccessor.Exists(GetPath(schemaName)));
    }

    [Theory]
    [InlineData(SharedSchemaName, SharedActivity)]
    [InlineData(OwnedSchemaName, UnreferencedActivity)]
    public async Task Pull_WhenTheCloudRepeatsAnUnchangedEnvironmentVariable_KeepsTheLocalEditAndCachesTheServerCopy(string schemaName, string activity)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current").WithVersion(200);
        var serverCopy = environmentVariable.WithVersion(201);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue("edited"));
        var editedFile = fileAccessor.Files[GetPath(schemaName).ToString()];

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(serverCopy) }));

        Assert.Equal(editedFile, fileAccessor.Files[GetPath(schemaName).ToString()]);
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
    }

    [Fact]
    public async Task Pull_WhenNoComponentReferencesAnEditedSharedEnvironmentVariable_KeepsItsFileForTheNextPush()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        WriteEnvironmentVariableFile(fileAccessor, shared.WithDefaultValue("edited"));
        ReplaceText(fileAccessor, FindTopicPath(fileAccessor), "Env.cr9f2_shared", "Topic.unused");

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2"));

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);
        Assert.Equal("edited", (Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(changeSet.EnvironmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.")).DefaultValue);
    }

    [Theory]
    [InlineData(SharedSchemaName, SharedActivity, false)]
    [InlineData(SharedSchemaName, SharedActivity, true)]
    [InlineData(OwnedSchemaName, UnreferencedActivity, false)]
    public async Task Pull_WhenLocalAndCloudEditDifferentEnvironmentVariableFields_WritesAndReturnsBothEditsWithoutAConflict(string schemaName, string activity, bool removeReference)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current").WithVersion(200);
        var serverCopy = environmentVariable.WithDisplayName("Cloud").WithVersion(201);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue("local"));
        if (removeReference)
        {
            ReplaceText(fileAccessor, FindTopicPath(fileAccessor), "Env.cr9f2_shared", "Topic.unused");
        }

        var conflicts = new List<WorkspaceDiagnostic>();

        var pulledDefinition = await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(serverCopy) }), conflicts);

        var merged = ReadEnvironmentVariableFile(fileAccessor, GetPath(schemaName));
        Assert.Equal("local", merged.DefaultValue);
        Assert.Equal("Cloud", merged.DisplayName);
        Assert.Equal("current", merged.ValueComponent?.Value);
        Assert.Equal(serverCopy.Version, merged.Version);
        Assert.Empty(conflicts);
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
        Assert.True(merged.Equals(Assert.Single(pulledDefinition.EnvironmentVariables, pulled => pulled.SchemaName.Value == schemaName), NodeComparison.Structural));
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, pulledDefinition, CancellationToken.None);
        var pending = Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(changeSet.EnvironmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
        Assert.Equal("local", pending.DefaultValue);
        Assert.Equal("Cloud", pending.DisplayName);
    }

    [Theory]
    [InlineData(SharedSchemaName, SharedActivity)]
    [InlineData(OwnedSchemaName, UnreferencedActivity)]
    public async Task Pull_WhenLocalAndCloudEditTheSameEnvironmentVariableField_WritesAConflictThatBlocksTheNextRead(string schemaName, string activity)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current").WithVersion(200);
        var serverCopy = environmentVariable.WithDefaultValue("cloud").WithVersion(201);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue("local"));
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(serverCopy) }), conflicts);

        var path = GetPath(schemaName).ToString();
        var text = ReadText(fileAccessor, GetPath(schemaName));
        Assert.True(McsConflictMarkers.Contains(text));
        Assert.Contains("defaultValue: local", text, StringComparison.Ordinal);
        Assert.Contains("defaultValue: cloud", text, StringComparison.Ordinal);
        var diagnostic = Assert.Single(conflicts);
        Assert.Equal(path, diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        Assert.Equal(McsConflictMarkers.FindFirstMarkerLine(text), diagnostic.Line);
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None));
        Assert.Equal(path, Assert.Single(failure.Diagnostics).FilePath);
    }

    [Theory]
    [InlineData(SharedSchemaName, SharedActivity)]
    [InlineData(OwnedSchemaName, UnreferencedActivity)]
    public async Task Pull_WhenCloudDeletesALocallyEditedEnvironmentVariable_KeepsTheEditInAConflict(string schemaName, string activity)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue("local"));
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { CreateDelete(environmentVariable) }), conflicts);

        var text = ReadText(fileAccessor, GetPath(schemaName));
        Assert.True(McsConflictMarkers.Contains(text));
        Assert.Contains("defaultValue: local", text, StringComparison.Ordinal);
        Assert.Equal(GetPath(schemaName).ToString(), Assert.Single(conflicts).FilePath);
        Assert.Empty(ReadCache(fileAccessor).EnvironmentVariables);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pull_WhenTheCloudKeepsALocallyDeletedEnvironmentVariable_KeepsItDeletedForTheNextPush(bool cloudRepeatsIt)
    {
        var environmentVariable = CreateEnvironmentVariable(OwnedSchemaName, value: "current").WithVersion(200);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) }, environmentVariable);
        fileAccessor.Delete(GetPath(OwnedSchemaName));

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: cloudRepeatsIt ? new[] { new EnvironmentVariableUpdate(environmentVariable.WithVersion(201)) } : null));

        Assert.False(fileAccessor.Exists(GetPath(OwnedSchemaName)));
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);
        Assert.Equal(environmentVariable.Id, Assert.IsType<EnvironmentVariableDelete>(Assert.Single(changeSet.EnvironmentVariableChanges)).DefinitionId);
    }

    [Fact]
    public async Task Pull_WhenTheCloudEditsALocallyDeletedEnvironmentVariable_WritesAConflict()
    {
        var environmentVariable = CreateEnvironmentVariable(OwnedSchemaName, value: "current").WithVersion(200);
        var serverCopy = environmentVariable.WithDefaultValue("cloud").WithVersion(201);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) }, environmentVariable);
        fileAccessor.Delete(GetPath(OwnedSchemaName));
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(serverCopy) }), conflicts);

        var text = ReadText(fileAccessor, GetPath(OwnedSchemaName));
        Assert.True(McsConflictMarkers.Contains(text));
        Assert.Contains("defaultValue: cloud", text, StringComparison.Ordinal);
        var diagnostic = Assert.Single(conflicts);
        Assert.Equal(GetPath(OwnedSchemaName).ToString(), diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData(null, false)]
    public async Task Pull_WhenAMissingOwnedEnvironmentVariableHoldsNoPendingDelete_WritesItBack(string? value, bool clearSyncBaselines)
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) }, CreateEnvironmentVariable(OwnedSchemaName, value: value));
        var cloned = fileAccessor.Files[GetPath(OwnedSchemaName).ToString()];
        fileAccessor.Delete(GetPath(OwnedSchemaName));
        if (clearSyncBaselines)
        {
            synchronizer.ClearComponentSyncBaselines(workspace);
        }

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);
        Assert.Empty(changeSet.EnvironmentVariableChanges);

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2"));

        Assert.Equal(cloned, fileAccessor.Files[GetPath(OwnedSchemaName).ToString()]);
    }

    [Theory]
    [InlineData(OwnedSchemaName, UnreferencedActivity, false)]
    [InlineData(OwnedSchemaName, UnreferencedActivity, true)]
    [InlineData(SharedSchemaName, SharedActivity, false)]
    [InlineData(SharedSchemaName, SharedActivity, true)]
    public async Task Pull_WhenTheCloudRecreatesAnUnchangedEnvironmentVariable_KeepsItsFileAndBindsItToTheNewDefinition(string schemaName, string activity, bool editLocally)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current");
        var recreated = CreateEnvironmentVariable(schemaName, value: "current");
        var (dataverse, lookups) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        var defaultValue = editLocally ? "edited" : environmentVariable.DefaultValue;
        if (editLocally)
        {
            WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue(defaultValue));
        }

        lookups.Clear();
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new EnvironmentVariableChange[] { CreateDelete(environmentVariable), new EnvironmentVariableInsert(recreated) }), conflicts);

        Assert.Equal(defaultValue, ReadEnvironmentVariableFile(fileAccessor, GetPath(schemaName)).DefaultValue);
        Assert.Empty(conflicts);
        Assert.Empty(lookups);
        Assert.True(recreated.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);
        Assert.Equal(editLocally ? new[] { recreated.Id.Value } : Array.Empty<Guid>(), changeSet.EnvironmentVariableChanges.Select(change => (Assert.IsType<EnvironmentVariableUpdate>(change).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.")).Id.Value));
    }

    [Theory]
    [InlineData(OwnedSchemaName, UnreferencedActivity)]
    [InlineData(SharedSchemaName, SharedActivity)]
    public async Task Pull_WhenTheCloudDeletesARecreatedEnvironmentVariable_RemovesTheFileThatStillCarriesTheOldIdentifier(string schemaName, string activity)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current");
        var recreated = CreateEnvironmentVariable(schemaName, value: "current");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: new EnvironmentVariableChange[] { CreateDelete(environmentVariable), new EnvironmentVariableInsert(recreated) }));
        var projected = ReadEnvironmentVariableFile(fileAccessor, GetPath(schemaName));
        Assert.Equal(environmentVariable.Id, projected.Id);

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-3", environmentVariableChanges: new[] { CreateDelete(recreated) }), localDefinition: (await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None)).WithEnvironmentVariables(new[] { projected }));

        Assert.False(fileAccessor.Exists(GetPath(schemaName)));
        Assert.Empty(ReadCache(fileAccessor).EnvironmentVariables);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pull_WhenTheCloudRecreatesALocallyEditedEnvironmentVariable_MergesTheEditIntoTheNewDefinitionInAnyOrder(bool deleteFirst)
    {
        var environmentVariable = CreateEnvironmentVariable(SharedSchemaName, value: "current");
        var recreated = CreateEnvironmentVariable(SharedSchemaName, value: "current").WithDisplayName("Cloud");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, environmentVariable);
        WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue("local"));
        var delete = CreateDelete(environmentVariable);
        var insert = new EnvironmentVariableInsert(recreated);
        var conflicts = new List<WorkspaceDiagnostic>();

        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-2", environmentVariableChanges: deleteFirst ? new EnvironmentVariableChange[] { delete, insert } : new EnvironmentVariableChange[] { insert, delete }), conflicts);

        var text = ReadText(fileAccessor, GetPath(SharedSchemaName));
        Assert.False(McsConflictMarkers.Contains(text));
        var merged = ReadEnvironmentVariableFile(fileAccessor, GetPath(SharedSchemaName));
        Assert.Equal("local", merged.DefaultValue);
        Assert.Equal("Cloud", merged.DisplayName);
        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ConflictedEnvironmentVariableFile_ReportsItAndKeepsTheCachedCopy()
    {
        var environmentVariable = CreateEnvironmentVariable(OwnedSchemaName, value: "current");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) }, environmentVariable);
        WriteText(fileAccessor, GetPath(OwnedSchemaName), McsConflictMarkers.Build("defaultValue: local", "defaultValue: cloud"));
        var diagnostics = new SyncDiagnosticsCollector();

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false, diagnostics, validate: false);

        var diagnostic = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(GetPath(OwnedSchemaName).ToString(), diagnostic.FilePath);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
        Assert.True(environmentVariable.Equals(Assert.Single(definition.EnvironmentVariables), NodeComparison.Structural));
    }

    [Fact]
    public async Task DiscardLocalChanges_ConflictedEnvironmentVariableFile_RestoresTheCachedCopy()
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) }, CreateEnvironmentVariable(OwnedSchemaName, value: "current"));
        var clonedFile = fileAccessor.Files[GetPath(OwnedSchemaName).ToString()];
        WriteText(fileAccessor, GetPath(OwnedSchemaName), McsConflictMarkers.Build("defaultValue: local", "defaultValue: cloud"));

        var (_, unreadable) = await synchronizer.ReadWorkspaceDefinitionForDiscardAsync(workspace, CancellationToken.None);
        var result = synchronizer.DiscardLocalChanges(workspace, unreadable);

        var change = Assert.Single(unreadable);
        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Equal(OwnedSchemaName, change.SchemaName);
        Assert.Equal(GetPath(OwnedSchemaName).ToString(), change.Uri);
        Assert.Equal(1, result.Restored);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(clonedFile, fileAccessor.Files[GetPath(OwnedSchemaName).ToString()]);
    }

    [Fact]
    public async Task DiscardLocalChanges_UnreadableNewEnvironmentVariableFile_DeletesIt()
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", UnreferencedActivity) });
        WriteText(fileAccessor, GetPath(OwnedSchemaName), McsConflictMarkers.Build("defaultValue: local", "defaultValue: cloud"));

        var (_, unreadable) = await synchronizer.ReadWorkspaceDefinitionForDiscardAsync(workspace, CancellationToken.None);
        var result = synchronizer.DiscardLocalChanges(workspace, unreadable);

        Assert.Equal(ChangeType.Create, Assert.Single(unreadable).ChangeType);
        Assert.Equal(1, result.Deleted);
        Assert.False(fileAccessor.Exists(GetPath(OwnedSchemaName)));
    }

    [Fact]
    public async Task RetargetSync_KeepsProjectedSharedEnvironmentVariableThatTargetComponentsDoNotReference()
    {
        var (synchronizer, fileAccessor, island, workspace) = CreateWorkspace(CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"));
        var original = fileAccessor.Files[SharedPath.ToString()];
        SetupRemoteChanges(island, CreateChangeSet("token-1", environmentVariableChanges: new[] { new EnvironmentVariableInsert(CreateEnvironmentVariable(SharedSchemaName, defaultValue: "target")) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse();

        await RetargetSyncAsync(synchronizer, workspace, dataverse);

        Assert.Equal(original, fileAccessor.Files[SharedPath.ToString()]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetargetSync_RemovesOnlyUneditedSharedEnvironmentVariableFilesThatTheTargetHolds(bool updateWorkspaceDirectory)
    {
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_managed} {Env.cr9f2_edited} {Env.cr9f2_absent}") }, CreateEnvironmentVariable(OwnedSchemaName), CreateEnvironmentVariable(SharedSchemaName), CreateEnvironmentVariable("cr9f2_managed"), CreateEnvironmentVariable("cr9f2_edited"), CreateEnvironmentVariable("cr9f2_absent"));
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable("cr9f2_edited", defaultValue: "edited"));
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", bot: CreateBotEntity()));
        var (dataverse, lookups) = CreateDataverse(TargetEnvironmentVariable(OwnedSchemaName, isManaged: true), TargetEnvironmentVariable(SharedSchemaName), TargetEnvironmentVariable("cr9f2_managed", isManaged: true), TargetEnvironmentVariable("cr9f2_edited", isManaged: true));

        await RetargetSyncAsync(synchronizer, workspace, dataverse, updateWorkspaceDirectory);

        Assert.True(fileAccessor.Exists(GetPath(OwnedSchemaName)));
        Assert.False(fileAccessor.Exists(SharedPath));
        Assert.False(fileAccessor.Exists(GetPath("cr9f2_managed")));
        Assert.True(fileAccessor.Exists(GetPath("cr9f2_edited")));
        Assert.True(fileAccessor.Exists(GetPath("cr9f2_absent")));
        Assert.Equal(new[] { "cr9f2_absent", "cr9f2_managed", SharedSchemaName }, Assert.Single(lookups).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RetargetSync_KeepsASharedEnvironmentVariableFileEditedWhileTheTargetLookupIsPending()
    {
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_unedited}") }, CreateEnvironmentVariable(SharedSchemaName), CreateEnvironmentVariable("cr9f2_unedited"));
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", bot: CreateBotEntity()));
        var (dataverse, lookups) = CreateDataverse(() => WriteEnvironmentVariableFile(fileAccessor, ReadEnvironmentVariableFile(fileAccessor, SharedPath).WithDefaultValue("edited")), TargetEnvironmentVariable(SharedSchemaName), TargetEnvironmentVariable("cr9f2_unedited"));

        await RetargetSyncAsync(synchronizer, workspace, dataverse, updateWorkspaceDirectory: false);

        Assert.True(fileAccessor.Exists(SharedPath));
        Assert.Equal("edited", ReadEnvironmentVariableFile(fileAccessor, SharedPath).DefaultValue);
        Assert.False(fileAccessor.Exists(GetPath("cr9f2_unedited")));
        Assert.Single(lookups);
    }

    [Fact]
    public async Task SyncWorkspace_WithoutARetargetInProgress_KeepsSharedEnvironmentVariableFilesThatTheTargetHolds()
    {
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_managed}") }, CreateEnvironmentVariable(SharedSchemaName), CreateEnvironmentVariable("cr9f2_managed"));
        SetupRemoteChanges(island, CreateChangeSet("token-2", bot: CreateBotEntity()));
        var (dataverse, lookups) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName), TargetEnvironmentVariable("cr9f2_managed", isManaged: true));

        await RetargetSyncAsync(synchronizer, workspace, dataverse, updateWorkspaceDirectory: false);

        Assert.True(fileAccessor.Exists(SharedPath));
        Assert.True(fileAccessor.Exists(GetPath("cr9f2_managed")));
        Assert.Empty(lookups);
    }

    [Fact]
    public async Task RetargetRollback_RestoresTheSharedEnvironmentVariableFileThatTheRetargetRemoved()
    {
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"));
        var original = fileAccessor.Files[SharedPath.ToString()];
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", bot: CreateBotEntity()));
        await RetargetSyncAsync(synchronizer, workspace, CreateDataverse(TargetEnvironmentVariable(SharedSchemaName, isManaged: true)).Dataverse, updateWorkspaceDirectory: false);
        Assert.False(fileAccessor.Exists(SharedPath));

        Assert.True(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));

        Assert.Equal(original, fileAccessor.Files[SharedPath.ToString()]);
    }

    [Fact]
    public async Task RetargetRollback_KeepsACapturedEnvironmentVariableFileEditedDuringTheRetargetAndRestoresTheOneTheRetargetReplaced()
    {
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, CreateEnvironmentVariable(OwnedSchemaName, defaultValue: "source"), CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"));
        var original = fileAccessor.Files[GetPath(OwnedSchemaName).ToString()];
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableInsert(CreateEnvironmentVariable(OwnedSchemaName, defaultValue: "target")) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse(() => WriteEnvironmentVariableFile(fileAccessor, ReadEnvironmentVariableFile(fileAccessor, SharedPath).WithDefaultValue("edited during the retarget")), TargetEnvironmentVariable(SharedSchemaName));
        await RetargetSyncAsync(synchronizer, workspace, dataverse);
        Assert.Equal("target", ReadEnvironmentVariableFile(fileAccessor, GetPath(OwnedSchemaName)).DefaultValue);

        Assert.True(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));

        Assert.Equal("edited during the retarget", ReadEnvironmentVariableFile(fileAccessor, SharedPath).DefaultValue);
        Assert.Equal(original, fileAccessor.Files[GetPath(OwnedSchemaName).ToString()]);
    }

    [Fact]
    public async Task RetargetRollback_RemovesTheUneditedEnvironmentVariableFilesTheRetargetProjectedAndKeepsOtherLocalFiles()
    {
        var (synchronizer, fileAccessor, island, workspace) = CreateWorkspace();
        var edited = CreateEnvironmentVariable("cr123.edited", value: "target");
        SetupRemoteChanges(island, CreateChangeSet("token-1", environmentVariableChanges: new[] { new EnvironmentVariableInsert(CreateEnvironmentVariable(OwnedSchemaName, value: "target")), new EnvironmentVariableInsert(edited) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse();
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        await RetargetSyncAsync(synchronizer, workspace, dataverse);
        Assert.True(fileAccessor.Exists(GetPath(OwnedSchemaName)));
        WriteEnvironmentVariableFile(fileAccessor, edited.WithDefaultValue("edited during the retarget"));
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable("cr123.local", value: "local"));

        Assert.True(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));

        Assert.False(fileAccessor.Exists(GetPath(OwnedSchemaName)));
        Assert.True(fileAccessor.Exists(GetPath("cr123.edited")));
        Assert.True(fileAccessor.Exists(GetPath("cr123.local")));
    }

    [Fact]
    public async Task RetargetRollback_WhenTheCachedCloudCopyIsUnreadable_RestoresTheBindingAndKeepsTheEnvironmentVariableFiles()
    {
        var (synchronizer, fileAccessor, island, workspace) = CreateWorkspace(CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"));
        SetupRemoteChanges(island, CreateChangeSet("token-1", environmentVariableChanges: new[] { new EnvironmentVariableInsert(CreateEnvironmentVariable(OwnedSchemaName, value: "target")) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse();
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        await RetargetSyncAsync(synchronizer, workspace, dataverse);
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "edited during the retarget"));
        WriteText(fileAccessor, CachePath, "{ not json");

        Assert.True(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));

        Assert.True(fileAccessor.Exists(GetPath(OwnedSchemaName)));
        Assert.Equal("edited during the retarget", ReadEnvironmentVariableFile(fileAccessor, SharedPath).DefaultValue);
        Assert.False(fileAccessor.Exists(CachePath));
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

    [Theory]
    [InlineData(OwnedSchemaName, UnreferencedActivity)]
    [InlineData(SharedSchemaName, SharedActivity)]
    public async Task GetRemoteChanges_WhenCloudDeletesAnEnvironmentVariableWithoutACurrentValue_ReportsTheDelete(string schemaName, string activity)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, _, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) }, environmentVariable);
        SetupRemoteChanges(island, CreateChangeSet("token-2", environmentVariableChanges: new[] { CreateDelete(environmentVariable) }));

        var (_, changes) = await synchronizer.GetRemoteChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var change = Assert.Single(changes, change => change.SchemaName == schemaName);
        Assert.Equal(ChangeType.Delete, change.ChangeType);
        Assert.Equal(GetPath(schemaName).ToString(), change.Uri);
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
