// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class RetargetWorkflowPreservationTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly IFileAccessor _fileAccessor;
    private readonly AgentSyncInfo _syncInfo = new() { AgentId = Guid.NewGuid() };

    public RetargetWorkflowPreservationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-retarget-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
        _fileAccessor = new FileAccessorFactory().Create(_workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WorkspaceSynchronizer CreateSynchronizer()
    {
        var fileParser = new SyncMcsFileParser(LspProjectorService.Instance);
        var fileAccessorFactory = new FileAccessorFactory();
        var island = new Mock<IIslandControlPlaneService>();
        var progress = new TestSyncProgress(new List<string>());
        var pathResolver = new LspComponentPathResolver();

        return new WorkspaceSynchronizer(fileParser, fileAccessorFactory, island.Object, progress, pathResolver);
    }

    private static Mock<ISyncDataverseClient> CreateEmptyRemoteDataverse()
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse
            .Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        dataverse
            .Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        return dataverse;
    }

    private string CreateLocalWorkflowFolder()
    {
        var workflowFolder = Path.Combine(_root, "workflows", "MyFlow-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(workflowFolder);
        File.WriteAllText(Path.Combine(workflowFolder, "metadata.yml"), "name: My Flow\n");
        File.WriteAllText(Path.Combine(workflowFolder, "workflow.json"), "{}");
        return workflowFolder;
    }

    private string CreateLocalPromptFolder()
    {
        var promptFolder = Path.Combine(_root, "prompts", "MyPrompt-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(promptFolder);
        File.WriteAllText(Path.Combine(promptFolder, "metadata.yml"), "name: My Prompt\n");
        return promptFolder;
    }

    private void StartRetarget(WorkspaceSynchronizer synchronizer)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".mcs"));
        synchronizer.PersistRetargetBackup(_workspace, RemoteBindingSnapshot.Empty);
    }

    [Fact]
    public async Task GetWorkflowsAsync_RemoteEmpty_PreservesUnpushedLocalWorkflowFolder()
    {
        var synchronizer = CreateSynchronizer();
        var workflowFolder = CreateLocalWorkflowFolder();
        var dataverse = CreateEmptyRemoteDataverse();

        await synchronizer.GetWorkflowsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        Assert.True(Directory.Exists(workflowFolder));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(workflowFolder, "workflow.json")));
    }

    [Fact]
    public async Task GetWorkflowsAsync_RemoteHasOtherWorkflows_PreservesUnpushedLocalWorkflowFolder()
    {
        var synchronizer = CreateSynchronizer();
        var workflowFolder = CreateLocalWorkflowFolder();
        var dataverse = CreateEmptyRemoteDataverse();
        dataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Other", ClientData = "{}" } });

        await synchronizer.GetWorkflowsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(workflowFolder, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(workflowFolder, "metadata.yml")));
    }

    [Fact]
    public async Task GetWorkflowsAsync_RemoteEmpty_DeletesPreviouslySyncedWorkflowFolder()
    {
        var synchronizer = CreateSynchronizer();
        var workflowFolder = CreateLocalWorkflowFolder();
        var workflowId = WorkflowWorkspace.WorkflowIdOf(Path.GetFileName(workflowFolder))!.Value;
        var (flow, _) = WorkspaceSynchronizer.GetFlowDefinition(new WorkflowMetadata { WorkflowId = workflowId, Name = "My Flow", ClientData = "{}" });
        WorkspaceSynchronizer.WriteCloudCache(_fileAccessor, new BotDefinition().WithFlows(new[] { flow }));
        var dataverse = CreateEmptyRemoteDataverse();

        await synchronizer.GetWorkflowsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        Assert.False(Directory.Exists(workflowFolder));
    }

    [Fact]
    public async Task GetWorkflowsAsync_PreservesDownloadedConnectionReferenceIdentity()
    {
        var synchronizer = CreateSynchronizer();
        var referenceId = Guid.NewGuid();
        var dataverse = CreateEmptyRemoteDataverse();
        dataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new WorkflowMetadata
                {
                    WorkflowId = Guid.NewGuid(),
                    Name = "Test",
                    ClientData = "{\"properties\":{\"connectionReferences\":{\"shared_approvals\":{\"connection\":{\"connectionReferenceLogicalName\":\"new_sharedapprovals_test\"}}}}}",
                },
            });
        dataverse.Setup(client => client.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ConnectionReferenceInfo
                {
                    ConnectionReferenceLogicalName = "new_sharedapprovals_test",
                    ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_approvals",
                    ConnectionId = "existing-connection",
                    ConnectionReferenceId = referenceId,
                    Version = 734,
                },
            });

        var metadata = await synchronizer.GetWorkflowsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        var reference = Assert.Single(metadata.ConnectionReferences);
        Assert.Equal(referenceId, reference.Id.Value);
        Assert.Equal(734, reference.Version);
        Assert.Equal("existing-connection", reference.ConnectionId);
    }

    [Fact]
    public async Task GetWorkflowsAsync_RemoteEmpty_DuringRetarget_PreservesLocalWorkflowFolder()
    {
        var synchronizer = CreateSynchronizer();
        var workflowFolder = CreateLocalWorkflowFolder();
        var dataverse = CreateEmptyRemoteDataverse();
        StartRetarget(synchronizer);

        await synchronizer.GetWorkflowsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        Assert.True(Directory.Exists(workflowFolder));
        Assert.True(File.Exists(Path.Combine(workflowFolder, "workflow.json")));
    }

    [Fact]
    public async Task GetAIPromptsAsync_RemoteEmpty_NotRetargeting_DeletesLocalPromptFolder()
    {
        var synchronizer = CreateSynchronizer();
        var promptFolder = CreateLocalPromptFolder();
        var dataverse = CreateEmptyRemoteDataverse();

        await synchronizer.GetAIPromptsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        Assert.False(Directory.Exists(promptFolder));
    }

    [Fact]
    public async Task GetAIPromptsAsync_RemoteEmpty_DuringRetarget_PreservesLocalPromptFolder()
    {
        var synchronizer = CreateSynchronizer();
        var promptFolder = CreateLocalPromptFolder();
        var dataverse = CreateEmptyRemoteDataverse();
        StartRetarget(synchronizer);

        await synchronizer.GetAIPromptsAsync(_workspace, dataverse.Object, _syncInfo, _fileAccessor, CancellationToken.None);

        Assert.True(Directory.Exists(promptFolder));
        Assert.True(File.Exists(Path.Combine(promptFolder, "metadata.yml")));
    }
}
