// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowReviewFixTests
{
    private const string Definition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static readonly Encoding NoByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static void Write(IFileAccessor workspace, string path, string content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, NoByteOrderMark);

        writer.Write(content);
    }

    private static string? Read(IFileAccessor workspace, string path)
    {
        var located = new AgentFilePath(path);

        if (!workspace.Exists(located))
        {
            return null;
        }

        using var stream = workspace.OpenRead(located);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static InMemoryFileAccessor WorkspaceWith(Guid workflowId, string name, string definition, int stateCode = 0)
    {
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor(name, workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", definition);
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata
        {
            WorkflowId = workflowId,
            Name = name,
            StateCode = stateCode,
            StatusCode = stateCode == 1 ? 2 : 1,
            JsonFileName = $"{folder}/{WorkflowWorkspace.DefinitionFileName}",
        }));

        return workspace;
    }

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name, string definition, int stateCode = 0) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = definition,
        StateCode = stateCode,
        StatusCode = stateCode == 1 ? 2 : 1,
    };

    [Fact]
    public async Task RollbackLeavesADraftCloudWorkflowAsADraft()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow", """{"properties":{"a":1}}"""));
        client.ConnectionReferences["new_teams"] = null;
        client.UpdateFailures.Enqueue("Connection 'new_teams' is not shared with the caller.");
        client.UpdateFailures.Enqueue("Connection 'new_teams' is not shared with the caller.");
        client.InsertFailure = "replacement refused";
        client.DeleteFailure = "delete refused";

        await Assert.ThrowsAnyAsync<StandaloneWorkflowException>(() =>
            new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "My Flow", Definition), "env", CancellationToken.None));

        Assert.Equal(0, (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).StateCode);
        Assert.DoesNotContain("start", client.Calls);
    }

    [Fact]
    public async Task ARefusedStartIsReportedRatherThanSilentlySucceeding()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow", """{"properties":{"a":1}}"""));
        client.ConnectionReferences["new_teams"] = "connection-1";
        client.StartFailure = "the caller may not enable this flow";

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "My Flow", Definition, stateCode: 1), "env", CancellationToken.None);

        Assert.Contains("refused", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Workflow.IsActivated);
    }

    [Fact]
    public async Task AConfirmedStartIsReportedAsRunning()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "My Flow", """{"properties":{"a":1}}"""));
        client.ConnectionReferences["new_teams"] = "connection-1";

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "My Flow", Definition, stateCode: 1), "env", CancellationToken.None);

        Assert.Equal(string.Empty, result.Warning);
        Assert.True(result.Workflow.IsActivated);
        Assert.Equal(1, result.Workflow.StateCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(null)]
    public async Task TheRawStateCodeSurvivesTheSharedContract(int? stateCode)
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "My Flow", ClientData = Definition, StateCode = stateCode });

        var document = await new StandaloneWorkflowSynchronizer(client).GetAsync(workflowId, CancellationToken.None);

        Assert.Equal(stateCode, (document ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).StateCode);
        Assert.Equal(stateCode == 1, document.IsActivated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(null)]
    public async Task ListKeepsTheRawStateCode(int? stateCode)
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "My Flow", ClientData = Definition, StateCode = stateCode });

        Assert.Equal(stateCode, Assert.Single(await new StandaloneWorkflowSynchronizer(client).ListAsync(null, null, CancellationToken.None)).StateCode);
    }

    [Fact]
    public async Task PullRemovesTheWorkflowAndItsCacheWhenTheServiceLostIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "My Flow", Definition));

        var synchronizer = new StandaloneWorkflowSynchronizer(client);
        var downloaded = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.NotNull(Read(workspace, $"{downloaded.Folder}/{WorkflowWorkspace.DefinitionFileName}"));
        Assert.NotEmpty(StandaloneWorkflowCache.Flows(workspace));

        await client.DeleteWorkflowAsync(workflowId, CancellationToken.None);

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.True(pulled.Deleted);
        Assert.Null(pulled.Download);
        Assert.Null(Read(workspace, $"{downloaded.Folder}/{WorkflowWorkspace.DefinitionFileName}"));
        Assert.Null(Read(workspace, $"{downloaded.Folder}/{WorkflowWorkspace.MetadataFileName}"));
        Assert.Empty(StandaloneWorkflowCache.Flows(workspace));
    }

    [Fact]
    public async Task PullWritesTheWorkflowWhenTheServiceStillHoldsIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "My Flow", Definition));

        var pulled = await new StandaloneWorkflowSynchronizer(client).PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.False(pulled.Deleted);
        Assert.NotNull(pulled.Download);
        Assert.NotNull(Read(workspace, $"{pulled.Download.Folder}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public void AnAgentProjectIsRecognisedThroughEitherCompoundExtension()
    {
        foreach (var fileName in new[] { "settings.mcs.yml", "settings.mcs.yaml", "agent.mcs.yml", "agent.mcs.yaml", "collection.mcs.yml", "collection.mcs.yaml" })
        {
            var workspace = NewWorkspace();

            Write(workspace, fileName, "kind: GptComponentMetadata\n");

            Assert.True(WorkflowWorkspace.HasAgentProject(workspace), fileName);
        }
    }

    [Fact]
    public void AWorkspaceWithoutAnAgentProjectIsNotOne()
    {
        var workspace = NewWorkspace();

        Write(workspace, "readme.md", "nothing");

        Assert.False(WorkflowWorkspace.HasAgentProject(workspace));
    }

    [Fact]
    public void ApplyStateRecordsBothCodes()
    {
        var running = new SyncDataverseClient.WorkflowMetadata { WorkflowId = Guid.NewGuid() };
        var drafted = new SyncDataverseClient.WorkflowMetadata { WorkflowId = Guid.NewGuid() };

        WorkflowActivationPlanner.ApplyState(running, activated: true);
        WorkflowActivationPlanner.ApplyState(drafted, activated: false);

        Assert.Equal(1, running.StateCode);
        Assert.Equal(2, running.StatusCode);
        Assert.Equal(0, drafted.StateCode);
        Assert.Equal(1, drafted.StatusCode);
    }
}
