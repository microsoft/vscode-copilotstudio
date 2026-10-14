// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkspaceClassifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-kind-" + Guid.NewGuid().ToString("N"));

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

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteWorkflow(bool withMetadata = true)
    {
        var folder = $"workflows/Flow-{Guid.NewGuid()}";

        WriteFile($"{folder}/workflow.json", Definition);

        if (withMetadata)
        {
            WriteFile($"{folder}/metadata.yml", "name: Flow");
        }
    }

    private void DeclareWorkflowProject() => WriteFile(WorkflowWorkspace.ProjectFileName, WorkflowWorkspace.BuildProjectFile());

    [Fact]
    public void ADeclaredWorkflowWorkspace_IsAStandaloneWorkflowWorkspace()
    {
        WriteWorkflow();
        DeclareWorkflowProject();

        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void ABareWorkflowsFolder_IsNotDeclaredAndStaysUnknown()
    {
        WriteWorkflow();

        Assert.Equal(WorkspaceKind.Unknown, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void TheLayoutMarkerAlone_DoesNotDeclareTheWorkspaceKind()
    {
        WriteWorkflow();
        WriteFile(AgentClassifier.WorkspaceLayoutMarkerFileName, AgentClassifier.BuildWorkspaceLayoutMarker());

        Assert.Equal(WorkspaceKind.Unknown, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void ADeclaredWorkspaceWhoseLastWorkflowWasDeleted_StaysAStandaloneWorkflowWorkspace()
    {
        DeclareWorkflowProject();

        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void AnAgentProjectOutranksTheWorkflowDeclaration()
    {
        WriteWorkflow();
        DeclareWorkflowProject();
        WriteFile("settings.mcs.yml", "displayName: Agent\n");

        Assert.Equal(WorkspaceKind.Agent, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void TheCliShapeWithoutItsSettings_IsAStandaloneWorkflowWorkspace()
    {
        WriteWorkflow();
        DeclareWorkflowProject();
        WriteFile(AgentClassifier.WorkspaceLayoutMarkerFileName, AgentClassifier.BuildWorkspaceLayoutMarker());
        WriteFile($"{ConnectionReferenceStore.LayeredFolderName}/new_teams{ConnectionReferenceStore.LayeredFileExtension}", "connectionReferences:\n  - connectionReferenceLogicalName: new_teams\n");

        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(_root));
    }

    [Theory]
    [InlineData("settings.mcs.yml")]
    [InlineData("agent.mcs.yml")]
    [InlineData("collection.mcs.yml")]
    public void AWorkspaceCarryingAnAgentProject_IsAnAgentWorkspace(string projectFileName)
    {
        WriteWorkflow();
        WriteFile(projectFileName, "displayName: Agent");

        Assert.Equal(WorkspaceKind.Agent, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void TheCliAgentShape_StaysAnAgentWorkspace()
    {
        WriteWorkflow();
        WriteFile("settings.mcs.yml", "displayName: NB1\nschemaName: crf9a_nb1\n");
        WriteFile(AgentClassifier.WorkspaceLayoutMarkerFileName, AgentClassifier.BuildWorkspaceLayoutMarker());
        WriteFile($"{ConnectionReferenceStore.LayeredFolderName}/new_teams{ConnectionReferenceStore.LayeredFileExtension}", "connectionReferences:\n  - connectionReferenceLogicalName: new_teams\n");

        Assert.Equal(WorkspaceKind.Agent, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void TheClassicAgentShape_StaysAnAgentWorkspace()
    {
        WriteWorkflow();
        WriteFile("agent.mcs.yml", "instructions: hello");
        WriteFile("settings.mcs.yml", "displayName: Agent D1\n");
        WriteFile(ConnectionReferenceStore.FlatFileName, "connectionReferences:\n  - connectionReferenceLogicalName: new_teams\n");

        Assert.Equal(WorkspaceKind.Agent, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void AHalfWrittenWorkflow_IsNotAStandaloneWorkflowWorkspace()
    {
        WriteWorkflow(withMetadata: false);

        Assert.Equal(WorkspaceKind.Unknown, WorkspaceClassifier.DetectKind(_root));
    }

    [Fact]
    public void AnEmptyDirectory_IsNeither()
    {
        Directory.CreateDirectory(_root);

        Assert.Equal(WorkspaceKind.Unknown, WorkspaceClassifier.DetectKind(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoWorkspaceRoot_IsNeither(string? workspaceRoot) =>
        Assert.Equal(WorkspaceKind.Unknown, WorkspaceClassifier.DetectKind(workspaceRoot));

    [Fact]
    public void AStandaloneWorkflowWorkspace_IsFullySupported()
    {
        WriteWorkflow();
        DeclareWorkflowProject();

        var classification = WorkspaceClassifier.Classify((Agents.ObjectModel.BotEntity?)null, _root);

        Assert.Equal(WorkspaceKind.StandaloneWorkflow, classification.Kind);
        Assert.Equal(AuthoringShape.CliCopilot, classification.AuthoringShape);
        Assert.Equal(WorkspaceLayout.CliLayered, classification.WorkspaceLayout);
        Assert.Equal(SupportLevel.Supported, classification.Support);
    }

    [Fact]
    public void AStandaloneWorkflowWorkspace_AllowsEveryOperation()
    {
        WriteWorkflow();
        DeclareWorkflowProject();

        var classification = WorkspaceClassifier.Classify((Agents.ObjectModel.BotEntity?)null, _root);

        foreach (var operation in Enum.GetValues<SyncOperation>())
        {
            Assert.True(classification.Allows(operation));
        }
    }

    [Fact]
    public void AStandaloneWorkflowWorkspace_AllowsPushWhereTheAgentClassifierAloneWouldNot()
    {
        WriteWorkflow();
        DeclareWorkflowProject();

        Assert.False(AgentClassifier.Classify((Agents.ObjectModel.DefinitionBase?)new Agents.ObjectModel.BotDefinition(), _root).Allows(SyncOperation.Push));
        Assert.True(WorkspaceClassifier.Classify((Agents.ObjectModel.DefinitionBase?)new Agents.ObjectModel.BotDefinition(), _root).Allows(SyncOperation.Push));
    }

    [Fact]
    public void AnAgentWorkspace_KeepsTheClassificationTheAgentClassifierGives()
    {
        WriteFile("settings.mcs.yml", "displayName: Agent\n");

        var shared = WorkspaceClassifier.Classify((Agents.ObjectModel.BotEntity?)null, _root);
        var agentOnly = AgentClassifier.Classify((Agents.ObjectModel.BotEntity?)null, _root);

        Assert.Equal(agentOnly.AuthoringShape, shared.AuthoringShape);
        Assert.Equal(agentOnly.WorkspaceLayout, shared.WorkspaceLayout);
        Assert.Equal(agentOnly.Support, shared.Support);
        Assert.Equal(WorkspaceKind.Agent, shared.Kind);
    }

    [Fact]
    public void AProvisionalShape_StillBlocksTheDestructiveOperations()
    {
        var provisional = new AgentClassification(AuthoringShape.Unknown, WorkspaceLayout.Unknown, SupportLevel.Provisional, "future", "test");

        Assert.True(provisional.Allows(SyncOperation.Inspect));
        Assert.True(provisional.Allows(SyncOperation.Clone));
        Assert.True(provisional.Allows(SyncOperation.Pull));
        Assert.False(provisional.Allows(SyncOperation.Push));
        Assert.False(provisional.Allows(SyncOperation.Reattach));

        Assert.Throws<InvalidOperationException>(() => AuthoringSupportGate.EnsureAllowed(provisional, SyncOperation.Push));
        AuthoringSupportGate.EnsureAllowed(provisional, SyncOperation.Pull);
    }

    [Fact]
    public void TheKindDefaultsToUnknownSoExistingCallersAreUnchanged() =>
        Assert.Equal(WorkspaceKind.Unknown, new AgentClassification(AuthoringShape.Classic, WorkspaceLayout.ClassicMcs, SupportLevel.Supported, null, "test").Kind);
}

public class StandaloneWorkflowPromotionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-promote-" + Guid.NewGuid().ToString("N"));

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

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string[] Snapshot() => Directory
        .GetFiles(_root, "*", SearchOption.AllDirectories)
        .Select(path => path.Substring(_root.Length + 1).Replace('\\', '/'))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private void ScaffoldStandaloneWorkspace()
    {
        var workflowId = Guid.NewGuid();

        WriteFile($"workflows/Flow-{workflowId}/workflow.json", Definition);
        WriteFile($"workflows/Flow-{workflowId}/metadata.yml", $"workflowId: {workflowId}\nname: Flow\n");
        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(_root);
    }

    [Fact]
    public void AStandaloneWorkspaceDeclaresItsConnectionReferencesWhereACliAgentDoes()
    {
        ScaffoldStandaloneWorkspace();

        Assert.Contains("infrastructure/connections/new_teams.sync.yaml", Snapshot());
        Assert.DoesNotContain("connectionreferences.mcs.yml", Snapshot());
    }

    [Fact]
    public void AddingAnAgentProjectPromotesTheWorkspaceWithoutMovingAnyFile()
    {
        ScaffoldStandaloneWorkspace();

        Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(_root));

        var beforePromotion = Snapshot();

        WriteFile("settings.mcs.yml", "displayName: Agent\n");

        var afterPromotion = Snapshot();

        Assert.Equal(WorkspaceKind.Agent, WorkspaceClassifier.DetectKind(_root));
        Assert.Equal(beforePromotion, afterPromotion.Where(path => path != "settings.mcs.yml").ToArray());
    }

    [Fact]
    public void ThePromotedWorkspaceKeepsTheConnectionReferencesTheWorkflowDeclared()
    {
        ScaffoldStandaloneWorkspace();
        WriteFile("settings.mcs.yml", "displayName: Agent\n");

        var declared = ConnectionReferenceStore.Read(WorkflowWorkspace.OpenWorkspace(_root), null, CancellationToken.None);

        Assert.Equal("new_teams", Assert.Single(declared).ConnectionReferenceLogicalName.Value);
    }

    [Fact]
    public void ThePromotedWorkspaceIsClassifiedExactlyAsTheAgentClassifierWould()
    {
        ScaffoldStandaloneWorkspace();
        WriteFile("settings.mcs.yml", "displayName: Agent\n");

        var promoted = WorkspaceClassifier.Classify((Agents.ObjectModel.BotEntity?)null, _root);
        var agentOnly = AgentClassifier.Classify((Agents.ObjectModel.BotEntity?)null, _root);

        Assert.Equal(WorkspaceKind.Agent, promoted.Kind);
        Assert.Equal(agentOnly.AuthoringShape, promoted.AuthoringShape);
        Assert.Equal(agentOnly.WorkspaceLayout, promoted.WorkspaceLayout);
        Assert.Equal(agentOnly.Support, promoted.Support);
    }
}
