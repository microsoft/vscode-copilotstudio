// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

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
        var envVar = CreateEnvVar("cr123_myVariable");
        var path = WorkspaceSynchronizer.GetEnvironmentVariablePath(envVar);
        Assert.Equal("environmentvariables/cr123_myVariable.mcs.yml", path.ToString());
    }

    [Fact]
    public async Task Clone_WritesEnvironmentVariableFiles()
    {
        // Arrange: clone returns a changeset with env var inserts
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var envVar1 = CreateEnvVar("cr123_apiEndpoint", "API Endpoint");
        var envVar2 = CreateEnvVar("cr123_maxRetries", "Max Retries");
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot")!;

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
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());

        // Act
        var referenceTracker = new ReferenceTracker();
        await synchronizer.CloneChangesAsync(
            workspace,
            referenceTracker,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            mockDataverse.Object,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert: env var files exist
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.True(fileAccessor.Exists(new AgentFilePath("environmentvariables/cr123_apiEndpoint.mcs.yml")),
            "First env var file should be projected");
        Assert.True(fileAccessor.Exists(new AgentFilePath("environmentvariables/cr123_maxRetries.mcs.yml")),
            "Second env var file should be projected");
    }

    [Fact]
    public async Task Clone_WithNoEnvVars_NoEnvVarFilesCreated()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot")!;
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
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());

        var referenceTracker = new ReferenceTracker();
        await synchronizer.CloneChangesAsync(
            workspace,
            referenceTracker,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            mockDataverse.Object,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert: no env var files
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var envVarFiles = fileAccessor.Files.Keys.Where(k => k.StartsWith("environmentvariables/")).ToList();
        Assert.Empty(envVarFiles);
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
