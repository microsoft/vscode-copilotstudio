// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowDiscardTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly Guid _workflowId = Guid.NewGuid();

    public StandaloneWorkflowDiscardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-standalone-discard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, WorkflowWorkspace.ProjectFileName), WorkflowWorkspace.BuildProjectFile());
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
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

    private static WorkspaceSynchronizer CreateSynchronizer() =>
        new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            new FileAccessorFactory(),
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

    private IFileAccessor Accessor() => new FileAccessorFactory().Create(_workspace);

    private static string Definition(string displayName) => "{\n  \"properties\": {\n    \"displayName\": \"" + displayName + "\"\n  }\n}";

    private string Metadata(string name) =>
        $"workflowId: {_workflowId}\n" +
        $"name: {name}\n" +
        "type: 1\n" +
        "category: 5\n" +
        "stateCode: 1\n" +
        "statusCode: 2\n";

    private string FolderFor(string name) => Path.Combine(_root, "workflows", WorkflowWorkspace.FolderNameFor(name, _workflowId));

    private void WriteLocal(string folderName, string metadata, string definition)
    {
        var folder = FolderFor(folderName);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.MetadataFileName), metadata);
        File.WriteAllText(Path.Combine(folder, WorkflowWorkspace.DefinitionFileName), definition);
    }

    private void WriteCache(string name, string definition)
    {
        var metadata = (WorkflowMetadataFile.ReadStrict(Metadata(name)) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        metadata.ClientData = definition;

        StandaloneWorkflowCache.Write(Accessor(), metadata);
    }

    [Fact]
    public async Task ARenameIsDiscardedBackIntoTheFolderTheWorkflowAlreadyOccupies()
    {
        WriteCache("Before", Definition("Original"));
        WriteLocal("Before", Metadata("After"), Definition("Original"));

        var synchronizer = CreateSynchronizer();
        var (changes, _) = await synchronizer.GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        Assert.NotEmpty(changes);

        var result = synchronizer.DiscardStandaloneWorkflowChanges(_workspace, changes);

        Assert.Equal(1, result.Restored);
        Assert.False(Directory.Exists(FolderFor("After")));
        Assert.Contains("name: Before", File.ReadAllText(Path.Combine(FolderFor("Before"), WorkflowWorkspace.MetadataFileName)), StringComparison.Ordinal);

        var (remaining, _) = await synchronizer.GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task MalformedMetadataIsReportedAsUnreadableRatherThanFailingTheDiscard()
    {
        WriteCache("My Flow", Definition("Original"));
        WriteLocal("My Flow", ": not yaml :\n\t- broken", Definition("Original"));

        var synchronizer = CreateSynchronizer();
        var (changes, unreadable) = await synchronizer.GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        Assert.Equal(2, unreadable.Length);

        var result = synchronizer.DiscardStandaloneWorkflowChanges(_workspace, [.. changes, .. unreadable]);

        Assert.True(result.Restored > 0);
        Assert.Contains("name: My Flow", File.ReadAllText(Path.Combine(FolderFor("My Flow"), WorkflowWorkspace.MetadataFileName)), StringComparison.Ordinal);

        var (remaining, remainingUnreadable) = await synchronizer.GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        Assert.Empty(remaining);
        Assert.Empty(remainingUnreadable);
    }

    [Fact]
    public async Task MalformedDefinitionJsonIsRecoveredByDiscard()
    {
        WriteCache("My Flow", Definition("Original"));
        WriteLocal("My Flow", Metadata("My Flow"), "{ not json");

        var synchronizer = CreateSynchronizer();
        var (changes, unreadable) = await synchronizer.GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        synchronizer.DiscardStandaloneWorkflowChanges(_workspace, [.. changes, .. unreadable]);

        Assert.Contains("Original", File.ReadAllText(Path.Combine(FolderFor("My Flow"), WorkflowWorkspace.DefinitionFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWellFormedWorkspaceReportsNothingUnreadable()
    {
        WriteCache("My Flow", Definition("Original"));
        WriteLocal("My Flow", Metadata("My Flow"), Definition("Edited"));

        var (changes, unreadable) = await CreateSynchronizer().GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        Assert.Empty(unreadable);
        Assert.Single(changes);
    }

    [Fact]
    public async Task ADeletedWorkflowIsRestoredUnderTheCachedName()
    {
        WriteCache("My Flow", Definition("Original"));

        var synchronizer = CreateSynchronizer();
        var (changes, _) = await synchronizer.GetStandaloneWorkflowChangesForDiscardAsync(_workspace, CancellationToken.None);

        Assert.Equal(2, synchronizer.DiscardStandaloneWorkflowChanges(_workspace, changes).Restored);
        Assert.True(File.Exists(Path.Combine(FolderFor("My Flow"), WorkflowWorkspace.DefinitionFileName)));
    }
}
