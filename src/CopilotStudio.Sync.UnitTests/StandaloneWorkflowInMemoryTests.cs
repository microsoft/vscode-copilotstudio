// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowInMemoryTests
{
    private const string Definition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static readonly DirectoryPath Root = new("c:/test/standalone-in-memory/");

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = Definition,
        StateCode = 0,
        StatusCode = 1,
    };

    private static void Write(IFileAccessor workspace, string path, string content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Read(IFileAccessor workspace, string path)
    {
        using var stream = workspace.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void InspectProject_OfAnEmptyWorkspace_IsFree()
    {
        using var factory = new ProductionFileAccessorFactory();

        Assert.Equal(WorkflowProjectHost.Free, WorkflowWorkspace.InspectProject(factory.Create(Root), Guid.NewGuid()));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("settings.mcs.yml")]
    public void InspectProject_OfAWorkspaceHoldingContentTheCloneDidNotWrite_IsOccupied(string path)
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        Write(workspace, path, "mine");

        Assert.Equal(WorkflowProjectHost.Occupied, WorkflowWorkspace.InspectProject(workspace, Guid.NewGuid()));
    }

    [Fact]
    public void InspectProject_OfAWorkspaceHoldingAnotherWorkflow_IsOccupied()
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        WorkflowScaffolder.Scaffold(workspace, "My Flow", Guid.NewGuid());

        Assert.Equal(WorkflowProjectHost.Occupied, WorkflowWorkspace.InspectProject(workspace, Guid.NewGuid()));
    }

    [Fact]
    public void InspectProject_OfAWorkspaceHoldingThisWorkflowNeverPushed_HasUnpushedChanges()
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        var workflowId = Guid.NewGuid();
        WorkflowScaffolder.Scaffold(workspace, "My Flow", workflowId);

        Assert.Equal(WorkflowProjectHost.UnpushedChanges, WorkflowWorkspace.InspectProject(workspace, workflowId));
    }

    [Fact]
    public async Task InspectProject_OfAWorkspaceHoldingThisWorkflowAsTheCloudHasIt_IsFree()
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        client.Seed(Row(workflowId, "My Flow"));

        await new StandaloneWorkflowSynchronizer(client, factory).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Equal(WorkflowProjectHost.Free, WorkflowWorkspace.InspectProject(workspace, workflowId));
    }

    [Fact]
    public void InspectProject_RequiresAWorkspace() =>
        Assert.Throws<ArgumentNullException>(() => WorkflowWorkspace.InspectProject((IFileAccessor)null!, Guid.NewGuid()));

    [Fact]
    public void Scaffold_IntoAWorkspace_WritesThePairWithoutAByteOrderMarkAndDeclaresTheProject()
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        var workflowId = Guid.NewGuid();

        var written = WorkflowScaffolder.Scaffold(workspace, "Scaffolded Flow", workflowId);

        Assert.Equal([$"workflows/ScaffoldedFlow-{workflowId}/workflow.json", $"workflows/ScaffoldedFlow-{workflowId}/metadata.yml", "agent.sync.yaml", "workflow.sync.yaml"], written);
        Assert.Equal(workflowId, WorkflowMetadataFile.ReadStrict(Read(workspace, written[1]))!.WorkflowId);
        Assert.Contains("Scaffolded Flow", Read(workspace, written[0]), StringComparison.Ordinal);
        Assert.All(written.Take(2), path =>
        {
            using var stream = workspace.OpenRead(new AgentFilePath(path));
            Assert.NotEqual(0xEF, stream.ReadByte());
        });
    }

    [Fact]
    public void Scaffold_IntoAnAgentWorkspace_WritesOnlyTheWorkflowPair()
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        Write(workspace, "settings.mcs.yml", "displayName: Agent");

        Assert.Equal(2, WorkflowScaffolder.Scaffold(workspace, "Scaffolded Flow", Guid.NewGuid()).Count);
        Assert.False(WorkflowWorkspace.HasProjectFile(workspace));
    }

    [Fact]
    public void Scaffold_IntoAWorkspaceWhereTheWorkflowAlreadyExists_KeepsWhatIsThere()
    {
        using var factory = new ProductionFileAccessorFactory();
        var workspace = factory.Create(Root);
        var workflowId = Guid.NewGuid();
        var definitionPath = WorkflowScaffolder.Scaffold(workspace, "Scaffolded Flow", workflowId)[0];
        Write(workspace, definitionPath, Definition);

        Assert.Throws<InvalidOperationException>(() => WorkflowScaffolder.Scaffold(workspace, "Scaffolded Flow", workflowId));
        Assert.Equal(Definition, Read(workspace, definitionPath));
    }

    [Fact]
    public void Scaffold_RequiresAWorkspace() =>
        Assert.Throws<ArgumentNullException>(() => WorkflowScaffolder.Scaffold((IFileAccessor)null!, "Flow", Guid.NewGuid()));

    [Fact]
    public void Synchronizer_RequiresAFileAccessorFactory() =>
        Assert.Throws<ArgumentNullException>(() => new StandaloneWorkflowSynchronizer(new FakeStandaloneWorkflowClient(), null!));

    [Fact]
    public async Task DownloadProject_ThroughAMemoryFactory_WritesTheProjectAndCachesWhatTheServiceReturned()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-op-");
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        client.Seed(Row(workflowId, "My Flow"));

        var project = await new StandaloneWorkflowSynchronizer(client, factory).DownloadProjectAsync(workflowId, session.Root.ToString(), "env", CancellationToken.None);

        var workspace = factory.Create(session.Root.GetChildDirectoryPath(project.ProjectFolder));
        Assert.Equal("MyFlow", project.ProjectFolder);
        Assert.Equal(workflowId, Assert.Single(WorkflowWorkspace.FindAll(workspace)).WorkflowId);
        Assert.True(WorkflowWorkspace.HasProjectFile(workspace));
        Assert.Equal(client.Stored(workflowId)!.ClientData, StandaloneWorkflowCache.Read(workspace, workflowId)!.ClientData);
    }

    [Fact]
    public async Task DownloadProject_ThroughAMemoryFactory_WhenUnrelatedContentHoldsTheName_ClonesBesideIt()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-op-");
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        client.Seed(Row(workflowId, "My Flow"));
        var occupied = factory.Create(session.Root.GetChildDirectoryPath("MyFlow"));
        Write(occupied, "notes.txt", "mine");

        Assert.Equal($"MyFlow-{workflowId}", (await new StandaloneWorkflowSynchronizer(client, factory).DownloadProjectAsync(workflowId, session.Root.ToString(), "env", CancellationToken.None)).ProjectFolder);
        Assert.Equal(["notes.txt"], occupied.ListFiles().Select(file => file.ToString()));
    }

    [Fact]
    public async Task DownloadProject_ThroughAMemoryFactory_WhenTheProjectHoldsUnpushedChanges_RefusesAndKeepsThem()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-op-");
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        client.Seed(Row(workflowId, "My Flow"));
        var synchronizer = new StandaloneWorkflowSynchronizer(client, factory);
        var workspace = factory.Create(session.Root.GetChildDirectoryPath((await synchronizer.DownloadProjectAsync(workflowId, session.Root.ToString(), "env", CancellationToken.None)).ProjectFolder));
        var definitionPath = WorkflowWorkspace.FindAll(workspace).Single().DefinitionPath.ToString();
        Write(workspace, definitionPath, """{"properties":{"edited":true}}""");

        Assert.Equal(StandaloneWorkflowFailure.ProjectHoldsUnpushedChanges, (await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.DownloadProjectAsync(workflowId, session.Root.ToString(), "env", CancellationToken.None))).Failure);
        Assert.Equal("""{"properties":{"edited":true}}""", Read(workspace, definitionPath));
    }

    [Fact]
    public async Task InMemorySyncServices_CloneStandaloneProjectsIntoTheMemoryFactory()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        client.Seed(Row(workflowId, "My Flow"));
        var services = new ServiceCollection();
        services.AddSyncServices(storageMode: SyncStorageMode.InMemory);
        services.AddSingleton<IStandaloneWorkflowDataverseClient>(client);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IFileAccessorFactory>();
        using var session = factory.LeaseTemporaryWorkspace("mcs-op-");

        Assert.Single(WorkflowWorkspace.FindAll(factory.Create(session.Root.GetChildDirectoryPath((await provider.GetRequiredService<IStandaloneWorkflowSynchronizer>().DownloadProjectAsync(workflowId, session.Root.ToString(), "env", CancellationToken.None)).ProjectFolder))));
    }
}
