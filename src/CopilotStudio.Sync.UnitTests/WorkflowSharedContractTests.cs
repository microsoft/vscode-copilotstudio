// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkflowWorkspaceAccessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-accessor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private const string DefinitionWithReferences = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static void Write(IFileAccessor fileAccessor, string path, string content)
    {
        using var stream = fileAccessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.Write(content);
    }

    private static InMemoryFileAccessor WorkspaceWith(string folderName, string definition, bool withMetadata = true)
    {
        var workspace = NewWorkspace();

        Write(workspace, $"workflows/{folderName}/workflow.json", definition);

        if (withMetadata)
        {
            Write(workspace, $"workflows/{folderName}/metadata.yml", WorkflowMetadataFile.Write(WorkflowScaffolder.NewMetadata("Flow", Guid.NewGuid(), $"workflows/{folderName}")));
        }

        return workspace;
    }

    private string WriteOnDisk(string folderName, string definition, bool withMetadata = true)
    {
        var folder = Path.Combine(_root, "workflows", folderName);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "workflow.json"), definition);

        if (withMetadata)
        {
            File.WriteAllText(Path.Combine(folder, "metadata.yml"), WorkflowMetadataFile.Write(WorkflowScaffolder.NewMetadata("Flow", Guid.NewGuid(), $"workflows/{folderName}")));
        }

        return folder;
    }

    [Fact]
    public void FindAll_OverTheAccessor_FindsTheSameWorkflowsAsOnDisk()
    {
        var workflowId = Guid.NewGuid();
        var folderName = $"Flow-{workflowId}";

        WriteOnDisk(folderName, DefinitionWithReferences);

        Assert.Equal(
            WorkflowWorkspace.FindAll(_root).Select(folder => (folder.WorkflowId, folder.FolderName)),
            WorkflowWorkspace.FindAll(WorkspaceWith(folderName, DefinitionWithReferences)).Select(file => (file.WorkflowId, file.FolderName)));
    }

    [Fact]
    public void FindAll_SkipsAFolderMissingItsMetadata() =>
        Assert.Empty(WorkflowWorkspace.FindAll(WorkspaceWith($"Flow-{Guid.NewGuid()}", DefinitionWithReferences, withMetadata: false)));

    [Fact]
    public void FindAll_SkipsAFolderThatDoesNotEndInAnIdentifier() =>
        Assert.Empty(WorkflowWorkspace.FindAll(WorkspaceWith("NotAWorkflow", DefinitionWithReferences)));

    [Fact]
    public void FindAll_SkipsANestedDefinition()
    {
        var workspace = NewWorkspace();

        Write(workspace, $"workflows/Flow-{Guid.NewGuid()}/nested/workflow.json", DefinitionWithReferences);

        Assert.Empty(WorkflowWorkspace.FindAll(workspace));
    }

    [Fact]
    public void FindAll_ReturnsWorkflowsInFolderNameOrder()
    {
        var workspace = NewWorkspace();
        var first = $"Alpha-{Guid.NewGuid()}";
        var second = $"Zulu-{Guid.NewGuid()}";

        Write(workspace, $"workflows/{second}/workflow.json", DefinitionWithReferences);
        Write(workspace, $"workflows/{second}/metadata.yml", "name: Zulu");
        Write(workspace, $"workflows/{first}/workflow.json", DefinitionWithReferences);
        Write(workspace, $"workflows/{first}/metadata.yml", "name: Alpha");

        Assert.Equal([first, second], WorkflowWorkspace.FindAll(workspace).Select(file => file.FolderName));
    }

    [Fact]
    public void HasIncompleteWorkflow_MatchesBetweenDiskAndAccessor()
    {
        var folderName = $"Flow-{Guid.NewGuid()}";

        WriteOnDisk(folderName, DefinitionWithReferences, withMetadata: false);

        Assert.True(WorkflowWorkspace.HasIncompleteWorkflow(_root));
        Assert.True(WorkflowWorkspace.HasIncompleteWorkflow(WorkspaceWith(folderName, DefinitionWithReferences, withMetadata: false)));
    }

    [Fact]
    public void HasIncompleteWorkflow_IgnoresAFolderThatNamesNoWorkflow()
    {
        var workspace = NewWorkspace();

        Write(workspace, "workflows/NotAWorkflow/readme.txt", "notes");

        Assert.False(WorkflowWorkspace.HasIncompleteWorkflow(workspace));
    }

    [Theory]
    [InlineData("settings.mcs.yml")]
    [InlineData("agent.mcs.yml")]
    [InlineData("collection.mcs.yml")]
    public void HasAgentProject_MatchesBetweenDiskAndAccessor(string projectFileName)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, projectFileName), "displayName: Agent");

        var workspace = NewWorkspace();
        Write(workspace, projectFileName, "displayName: Agent");

        Assert.True(WorkflowWorkspace.HasAgentProject(_root));
        Assert.True(WorkflowWorkspace.HasAgentProject(workspace));
    }

    [Fact]
    public void IsStandaloneWorkspace_MatchesBetweenDiskAndAccessor()
    {
        var folderName = $"Flow-{Guid.NewGuid()}";

        WriteOnDisk(folderName, DefinitionWithReferences);

        Assert.True(WorkflowWorkspace.IsStandaloneWorkspace(_root));
        Assert.True(WorkflowWorkspace.IsStandaloneWorkspace(WorkspaceWith(folderName, DefinitionWithReferences)));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_OverTheAccessor_WritesTheLayeredShape()
    {
        var workspace = WorkspaceWith($"Flow-{Guid.NewGuid()}", DefinitionWithReferences);

        Assert.True(WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(workspace));
        Assert.True(workspace.Exists(new AgentFilePath($"{ConnectionReferenceStore.LayeredFolderName}/new_teams{ConnectionReferenceStore.LayeredFileExtension}")));
        Assert.True(workspace.Exists(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName)));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_OverTheAccessor_IsRefusedOnAnAgentWorkspace()
    {
        var workspace = WorkspaceWith($"Flow-{Guid.NewGuid()}", DefinitionWithReferences);

        Write(workspace, "settings.mcs.yml", "displayName: Agent");

        Assert.Throws<ArgumentException>(() => WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(workspace));
    }

    [Fact]
    public void WriteConnectionReferenceDeclaration_RequiresAWorkspace() =>
        Assert.Throws<ArgumentNullException>(() => WorkflowDefinitionFile.WriteConnectionReferenceDeclaration((IFileAccessor)null!));

    [Fact]
    public void WorkflowIdOf_ReadsTheSameIdentifierTheRegexDid()
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal(workflowId, WorkflowWorkspace.WorkflowIdOf($"My Flow-{workflowId}"));
        Assert.Equal(workflowId, WorkflowWorkspace.WorkflowIdOf(workflowId.ToString()));
        Assert.Equal(workflowId, WorkflowWorkspace.WorkflowIdOf($"Flow-{workflowId.ToString().ToUpperInvariant()}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Flow")]
    [InlineData("Flow-not-a-guid")]
    [InlineData("00000000-0000-0000-0000-00000000000")]
    public void WorkflowIdOf_WithoutATrailingIdentifier_IsNull(string? folderName) =>
        Assert.Null(WorkflowWorkspace.WorkflowIdOf(folderName));
}

public class WorkflowDefinitionReadingTests
{
    private const string DefinitionWithBoundConnection = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"bound-in-source-environment"}}}}
    """;

    [Fact]
    public void Read_ParsesEveryFormFromTheSameText()
    {
        var forms = WorkflowDefinitionFile.Read(DefinitionWithBoundConnection);

        Assert.Equal(["new_teams"], forms.References.Select(reference => reference.LogicalName));
        Assert.DoesNotContain("connectionName", forms.Portable, StringComparison.Ordinal);
        Assert.Equal(WorkflowDefinitionFile.Canonicalize(DefinitionWithBoundConnection), forms.Canonical);
    }

    [Fact]
    public void Canonicalize_IgnoresFormattingOnlyEdits() =>
        Assert.Equal(
            WorkflowDefinitionFile.Canonicalize(DefinitionWithBoundConnection),
            WorkflowDefinitionFile.Canonicalize(WorkflowDefinitionFile.Format(DefinitionWithBoundConnection)));

    [Fact]
    public void Canonicalize_IsStableAcrossRepeatedCalls() =>
        Assert.Equal(
            WorkflowDefinitionFile.Canonicalize(DefinitionWithBoundConnection),
            WorkflowDefinitionFile.Canonicalize(WorkflowDefinitionFile.Canonicalize(DefinitionWithBoundConnection)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Read_OfNothingReadable_HasNoReferences(string? clientData) =>
        Assert.Empty(WorkflowDefinitionFile.Read(clientData).References);
}

public class ConnectionReferenceExtractorParityTests
{
    private static InMemoryFileAccessor WorkspaceWithDefinition(string definition)
    {
        var workspace = new InMemoryFileAccessor(new DirectoryPath("workspace"));
        var folder = $"workflows/Flow-{Guid.NewGuid()}";

        foreach (var (path, content) in new[] { ($"{folder}/workflow.json", definition), ($"{folder}/metadata.yml", "name: Flow") })
        {
            using var stream = workspace.OpenWrite(new AgentFilePath(path));
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            writer.Write(content);
        }

        return workspace;
    }

    private static ImmutableArray<string> ScannedNames(string definition) =>
        new ConnectionReferenceUsageScanner()
            .Scan(WorkspaceWithDefinition(definition), new Dictionary<string, string>(), CancellationToken.None)
            .Workflows
            .SelectMany(workflow => workflow.ConnectionReferenceLogicalNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    [Theory]
    [InlineData("""{"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}""")]
    [InlineData("""{"properties":{"connectionReferences":{"new_fallback":{"connection":{"connectionReferenceLogicalName":"new_fallback"}}}}}""")]
    [InlineData("""{"properties":{"connectionReferences":{}}}""")]
    [InlineData("""{"properties":{}}""")]
    [InlineData("{}")]
    [InlineData("{ not json")]
    [InlineData("")]
    public void TheScannerAndTheDefinitionFileAgree(string definition) =>
        Assert.Equal(
            WorkflowDefinitionFile.ConnectionReferenceNames(definition).Distinct(StringComparer.OrdinalIgnoreCase),
            ScannedNames(definition));

    [Fact]
    public void TheDefinitionFileFallsBackToTheKeyWhenNoApiIsNamed()
    {
        var only = Assert.Single(WorkflowDefinitionFile.ConnectionReferences("""{"properties":{"connectionReferences":{"shared_sql":{"connection":{"connectionReferenceLogicalName":"new_sql"}}}}}"""));

        Assert.Equal("shared_sql", only.ConnectorInternalId);
    }
}
