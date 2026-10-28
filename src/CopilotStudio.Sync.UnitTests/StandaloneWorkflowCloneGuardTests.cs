// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowCloneGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-clone-guard-" + Guid.NewGuid().ToString("N"));

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

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = Definition,
        StateCode = 0,
        StatusCode = 1,
    };

    private Task<StandaloneWorkflowProjectDownload> CloneAsync(FakeStandaloneWorkflowClient client, Guid workflowId) =>
        new StandaloneWorkflowSynchronizer(client).DownloadProjectAsync(workflowId, _root, "env", CancellationToken.None);

    private string EditDefinition(string projectRoot, Guid workflowId)
    {
        var definitionPath = WorkflowWorkspace.FindAll(projectRoot).Single(folder => folder.WorkflowId == workflowId).DefinitionPath;

        File.WriteAllText(definitionPath, """{"properties":{"edited":true}}""");

        return definitionPath;
    }

    [Fact]
    public async Task ReCloning_OverAProjectHoldingUnpushedWork_IsRefused()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var first = await CloneAsync(client, workflowId);
        var definitionPath = EditDefinition(first.ProjectRoot, workflowId);

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => CloneAsync(client, workflowId));

        Assert.Equal(StandaloneWorkflowFailure.ProjectHoldsUnpushedChanges, failure.Failure);
        Assert.Equal("""{"properties":{"edited":true}}""", File.ReadAllText(definitionPath));
    }

    [Fact]
    public async Task ReCloning_OverAProjectHoldingAnUnpushedRename_IsRefused()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var first = await CloneAsync(client, workflowId);
        var metadataPath = WorkflowWorkspace.FindAll(first.ProjectRoot).Single().MetadataPath;
        var metadata = (WorkflowMetadataFile.ReadStrict(File.ReadAllText(metadataPath)) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        metadata.Name = "Renamed Locally";

        File.WriteAllText(metadataPath, WorkflowMetadataFile.Write(metadata));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => CloneAsync(client, workflowId));

        Assert.Equal(StandaloneWorkflowFailure.ProjectHoldsUnpushedChanges, failure.Failure);
    }

    [Fact]
    public async Task ReCloning_OverACleanProject_StillReusesIt()
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
    public async Task Cloning_BesideAScaffoldedProjectOfTheSameName_LeavesTheScaffoldAlone()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var scaffolded = WorkflowScaffolder.ScaffoldProject(_root, "My Flow", Guid.NewGuid());

        var project = await CloneAsync(client, workflowId);

        Assert.NotEqual(scaffolded.ProjectFolder, project.ProjectFolder);
        Assert.Single(WorkflowWorkspace.FindAll(scaffolded.ProjectRoot));
        Assert.Single(WorkflowWorkspace.FindAll(project.ProjectRoot));
    }

    [Fact]
    public void InspectProject_ReportsUnpushedChangesForAScaffoldedWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var scaffolded = WorkflowScaffolder.ScaffoldProject(_root, "My Flow", workflowId);

        Assert.Equal(WorkflowProjectHost.UnpushedChanges, WorkflowWorkspace.InspectProject(scaffolded.ProjectRoot, workflowId));
        Assert.False(WorkflowWorkspace.CanHostProject(scaffolded.ProjectRoot, workflowId));
    }

    [Fact]
    public async Task InspectProject_ReportsFreeForACleanClone()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow"));

        var project = await CloneAsync(client, workflowId);

        Assert.Equal(WorkflowProjectHost.Free, WorkflowWorkspace.InspectProject(project.ProjectRoot, workflowId));
    }

    [Fact]
    public void InspectProject_ReportsOccupiedForAnotherWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var scaffolded = WorkflowScaffolder.ScaffoldProject(_root, "My Flow", workflowId);

        Assert.Equal(WorkflowProjectHost.Occupied, WorkflowWorkspace.InspectProject(scaffolded.ProjectRoot, Guid.NewGuid()));
    }

    [Fact]
    public void ScaffoldingTwiceIntoOneRoot_StillGivesEachWorkflowItsOwnProject()
    {
        var first = WorkflowScaffolder.ScaffoldProject(_root, "My Flow", Guid.NewGuid());
        var second = WorkflowScaffolder.ScaffoldProject(_root, "My Flow", Guid.NewGuid());

        Assert.NotEqual(first.ProjectFolder, second.ProjectFolder);
        Assert.Single(WorkflowWorkspace.FindAll(first.ProjectRoot));
        Assert.Single(WorkflowWorkspace.FindAll(second.ProjectRoot));
    }
}
