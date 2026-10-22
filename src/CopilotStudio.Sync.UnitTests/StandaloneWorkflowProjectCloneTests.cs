// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowProjectCloneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-project-clone-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private const string Definition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name, bool? isManaged = null) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = Definition,
        StateCode = 0,
        StatusCode = 1,
        IsManaged = isManaged,
    };

    private Task<StandaloneWorkflowProjectDownload> CloneAsync(FakeStandaloneWorkflowClient client, Guid workflowId) =>
        new StandaloneWorkflowSynchronizer(client).DownloadProjectAsync(workflowId, _root, "env", CancellationToken.None);

    [Fact]
    public async Task DownloadProject_OfAManagedFlow_IsRefusedWithoutCreatingAProjectFolder()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow", isManaged: true));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => CloneAsync(client, workflowId));

        Assert.Equal(StandaloneWorkflowFailure.Managed, failure.Failure);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task DownloadProject_CreatesAFolderNamedForTheWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var project = await CloneAsync(client, workflowId);

        Assert.Equal("MyFlow", project.ProjectFolder);
        Assert.True(Directory.Exists(Path.Combine(_root, "MyFlow")));
    }

    [Fact]
    public async Task DownloadProject_PlacesTheWorkflowBeneathTheProjectFolder()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var project = await CloneAsync(client, workflowId);
        var workflowFolder = Path.Combine(_root, project.ProjectFolder, "workflows", $"MyFlow-{workflowId}");

        Assert.True(File.Exists(Path.Combine(workflowFolder, WorkflowWorkspace.DefinitionFileName)));
        Assert.True(File.Exists(Path.Combine(workflowFolder, WorkflowWorkspace.MetadataFileName)));
    }

    [Fact]
    public async Task DownloadProject_DeclaresEachProjectAsItsOwnStandaloneWorkspace()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var project = await CloneAsync(client, workflowId);
        var projectRoot = Path.Combine(_root, project.ProjectFolder);

        Assert.True(File.Exists(Path.Combine(projectRoot, WorkflowWorkspace.ProjectFileName)));
        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(projectRoot));
        Assert.Equal(WorkspaceKind.Unknown, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public async Task DownloadProject_KeepsTheCacheInsideTheProject()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var project = await CloneAsync(client, workflowId);

        Assert.True(File.Exists(Path.Combine(_root, project.ProjectFolder, ".mcs", "botdefinition.json")));
        Assert.False(File.Exists(Path.Combine(_root, ".mcs", "botdefinition.json")));
    }

    [Fact]
    public async Task DownloadProject_TwoWorkflows_ShareARootWithoutColliding()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(first, "First Flow"));
        client.Seed(Row(second, "Second Flow"));

        var firstProject = await CloneAsync(client, first);
        var secondProject = await CloneAsync(client, second);

        Assert.NotEqual(firstProject.ProjectFolder, secondProject.ProjectFolder);
        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(Path.Combine(_root, firstProject.ProjectFolder)));
        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(Path.Combine(_root, secondProject.ProjectFolder)));
        Assert.Single(WorkflowWorkspace.FindAll(Path.Combine(_root, firstProject.ProjectFolder)));
        Assert.Single(WorkflowWorkspace.FindAll(Path.Combine(_root, secondProject.ProjectFolder)));
    }

    [Fact]
    public async Task DownloadProject_TwoWorkflowsWhoseNamesShareAFolderName_AreGivenSeparateProjects()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(first, "My Flow"));
        client.Seed(Row(second, "MyFlow"));

        var firstProject = await CloneAsync(client, first);
        var secondProject = await CloneAsync(client, second);

        Assert.Equal("MyFlow", firstProject.ProjectFolder);
        Assert.Equal($"MyFlow-{second}", secondProject.ProjectFolder);
        Assert.Single(WorkflowWorkspace.FindAll(firstProject.ProjectRoot));
        Assert.Single(WorkflowWorkspace.FindAll(secondProject.ProjectRoot));
    }

    [Fact]
    public async Task DownloadProject_TwoWorkflowsWhoseNamesShareAFolderName_StayReadableOnTheirOwn()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(first, "My Flow"));
        client.Seed(Row(second, "MyFlow"));

        var synchronizer = new StandaloneWorkflowSynchronizer(client);
        var firstProject = await CloneAsync(client, first);
        var secondProject = await CloneAsync(client, second);

        var firstLocal = await synchronizer.ReadLocalAsync(WorkflowWorkspace.OpenWorkspace(firstProject.ProjectRoot), CancellationToken.None);
        var secondLocal = await synchronizer.ReadLocalAsync(WorkflowWorkspace.OpenWorkspace(secondProject.ProjectRoot), CancellationToken.None);

        Assert.Equal(first, firstLocal.WorkflowId);
        Assert.Equal(second, secondLocal.WorkflowId);
    }

    [Fact]
    public async Task DownloadProject_CloningTheSameWorkflowAgain_ReusesItsProject()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var first = await CloneAsync(client, workflowId);
        var second = await CloneAsync(client, workflowId);

        Assert.Equal(first.ProjectFolder, second.ProjectFolder);
        Assert.Single(WorkflowWorkspace.FindAll(second.ProjectRoot));
    }

    [Fact]
    public async Task DownloadProject_WhenUnrelatedContentHoldsTheName_ClonesBesideIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));
        Directory.CreateDirectory(Path.Combine(_root, "MyFlow"));
        File.WriteAllText(Path.Combine(_root, "MyFlow", "notes.txt"), "mine");

        var project = await CloneAsync(client, workflowId);

        Assert.Equal($"MyFlow-{workflowId}", project.ProjectFolder);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_root, "MyFlow", "notes.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "MyFlow", WorkflowWorkspace.ProjectFileName)));
    }

    [Fact]
    public async Task DownloadProject_WhenAnAgentProjectHoldsTheName_ClonesBesideIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));
        Directory.CreateDirectory(Path.Combine(_root, "MyFlow"));
        File.WriteAllText(Path.Combine(_root, "MyFlow", "settings.mcs.yml"), "displayName: Agent");

        var project = await CloneAsync(client, workflowId);

        Assert.Equal($"MyFlow-{workflowId}", project.ProjectFolder);
        Assert.Single(WorkflowWorkspace.FindAll(project.ProjectRoot));
    }

    [Fact]
    public async Task DownloadProject_WhenNoFolderIsFree_ReportsTheOccupiedFolder()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        foreach (var occupied in new[] { "MyFlow", $"MyFlow-{workflowId}" })
        {
            Directory.CreateDirectory(Path.Combine(_root, occupied));
            File.WriteAllText(Path.Combine(_root, occupied, "settings.mcs.yml"), "displayName: Agent");
        }

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => CloneAsync(client, workflowId));

        Assert.Equal(StandaloneWorkflowFailure.ProjectFolderOccupied, failure.Failure);
    }

    [Fact]
    public async Task DownloadProject_ReportsTheProjectRootItWroteBeneath()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var project = await CloneAsync(client, workflowId);

        Assert.Equal(Path.Combine(_root, "MyFlow"), project.ProjectRoot);
        Assert.True(project.Download.Changed);
    }

    [Fact]
    public async Task DownloadProject_RejectsANameNoFolderCanCarry()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "///"));

        await Assert.ThrowsAsync<StandaloneWorkflowException>(() => CloneAsync(client, workflowId));
    }

    [Fact]
    public async Task DownloadProject_RejectsAWorkflowTheEnvironmentDoesNotHold() =>
        await Assert.ThrowsAsync<StandaloneWorkflowException>(() => CloneAsync(new FakeStandaloneWorkflowClient(), Guid.NewGuid()));

    [Fact]
    public async Task DownloadProject_RejectsNoRoot()
    {
        var client = new FakeStandaloneWorkflowClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new StandaloneWorkflowSynchronizer(client).DownloadProjectAsync(Guid.NewGuid(), string.Empty, "env", CancellationToken.None));
    }
}
