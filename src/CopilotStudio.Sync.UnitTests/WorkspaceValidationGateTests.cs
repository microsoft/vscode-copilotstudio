// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkspaceValidationGateTests
{
    private const string CliSettings =
        "displayName: Gate Test\n" +
        "schemaName: cr123_gatetest\n" +
        "configuration:\n" +
        "  recognizer:\n" +
        "    kind: CLICopilotRecognizer\n" +
        "  agentSettings:\n" +
        "    model:\n" +
        "      series: Sonnet46\n" +
        "template: cliagent-1.0.0\n" +
        "language: 1033\n";

    private static async Task<(WorkspaceSynchronizer Sync, InMemoryFileAccessor Accessor, DirectoryPath Workspace)> CreateWorkspaceAsync()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/validation-gate-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);
        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), "layoutVersion: 1\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"), CliSettings, CancellationToken.None);
        return (synchronizer, accessor, workspace);
    }

    private static void Write(InMemoryFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<(WorkspaceSynchronizer Sync, InMemoryFileAccessor Accessor, DirectoryPath Workspace)> CreateClonedWorkspaceAsync(ISyncProgress? progress = null)
    {
        var (synchronizer, factory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure(progress);
        var workspace = new DirectoryPath($"c:/test/validation-gate-clone-{Guid.NewGuid():N}/");

        var entity = CodeSerializer.Deserialize<Agents.ObjectModel.BotEntity>("kind: Bot\nschemaName: cr123_gatetest\ndisplayName: Gate Test\n")!;
        var component = new Agents.ObjectModel.DialogComponent(
            schemaName: "cr123_gatetest.topic.Broken",
            displayName: "Broken",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: CodeSerializer.Deserialize<Agents.ObjectModel.AdaptiveDialog>("kind: AdaptiveDialog\n")!);

        mockIsland
            .Setup(island => island.GetComponentsAsync(It.IsAny<Agents.Platform.Content.AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Agents.ObjectModel.PvaComponentChangeSet([new Agents.ObjectModel.BotComponentInsert(component)], entity, "token-1"));

        var mockDataverse = new Mock<Dataverse.ISyncDataverseClient>();
        mockDataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockDataverse.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), ComponentWriterDefensiveTests.CreateMockOperationContext(), mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        return (synchronizer, (InMemoryFileAccessor)factory.Create(workspace), workspace);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_ValidWorkspace_DoesNotThrow()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        Write(accessor, "behaviors/valid-skill/SKILL.md", "---\nname: valid\ndescription: A valid skill.\n---\nBody\n");

        Assert.NotNull(await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true));
    }

    [Fact]
    public void WorkspaceDiagnostic_WithPosition_FormatsFileAndPosition()
    {
        Assert.Equal("topics/Goodbye.mcs.yml(12,3): Duplicate key 'displayName'.", new WorkspaceDiagnostic("topics/Goodbye.mcs.yml", "Duplicate key 'displayName'.", 12, 3).ToString());
    }

    [Fact]
    public void WorkspaceDiagnostic_WithoutPosition_FormatsFileOnly()
    {
        Assert.Equal("topics/Goodbye.mcs.yml: Unreadable.", new WorkspaceDiagnostic("topics/Goodbye.mcs.yml", "Unreadable.", 0, 0).ToString());
    }

    [Fact]
    public void WorkspaceValidationException_ListsEveryDiagnostic()
    {
        var failure = new WorkspaceValidationException(
        [
            new WorkspaceDiagnostic("topics/One.mcs.yml", "Duplicate key 'a'.", 2, 1),
            new WorkspaceDiagnostic("topics/Two.mcs.yml", "Tabs are not allowed in indentation.", 5, 1),
        ]);

        Assert.Contains("2 workspace files could not be read", failure.Message, StringComparison.Ordinal);
        Assert.Contains("topics/One.mcs.yml(2,1)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("topics/Two.mcs.yml(5,1)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, failure.Diagnostics.Length);
    }

    [Fact]
    public void WorkspaceValidationException_SingleDiagnostic_UsesSingularWording()
    {
        Assert.Contains("1 workspace file could not be read", new WorkspaceValidationException([new WorkspaceDiagnostic("settings.mcs.yml", "Unreadable.", 0, 0)]).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("behaviors/skill-1/SKILL.md")]
    [InlineData("behaviors\\skill-1\\SKILL.md")]
    public void WorkspaceValidationException_DuplicateDiagnostic_IsReportedOnce(string duplicatePath)
    {
        var first = new WorkspaceDiagnostic("behaviors/skill-1/SKILL.md", McsConflictMarkers.Message, 6, 1, WorkspaceDiagnosticKind.MergeConflict);
        var failure = new WorkspaceValidationException(
        [
            first,
            new WorkspaceDiagnostic(duplicatePath, McsConflictMarkers.Message, 6, 1, WorkspaceDiagnosticKind.MergeConflict),
        ]);

        Assert.Same(first, Assert.Single(failure.Diagnostics));
        Assert.Equal("1 workspace file could not be read:" + Environment.NewLine + "  " + first, failure.Message);
    }

    [Fact]
    public void WorkspaceValidationException_DifferentProblemsInOneFile_CountsOneFile()
    {
        var first = new WorkspaceDiagnostic("topics/One.mcs.yml", "Duplicate key 'a'.", 2, 1);
        var second = new WorkspaceDiagnostic("topics\\One.mcs.yml", "Duplicate key 'b'.", 5, 1);
        var failure = new WorkspaceValidationException([first, second, first]);

        Assert.Equal(new[] { first, second }, failure.Diagnostics);
        Assert.Equal(
            "1 workspace file could not be read:" + Environment.NewLine + "  " + first + Environment.NewLine + "  " + second,
            failure.Message);
    }

    [Fact]
    public void WorkspaceValidationException_CaseDistinctPaths_RemainDistinct()
    {
        var failure = new WorkspaceValidationException(
        [
            new WorkspaceDiagnostic("topics/One.mcs.yml", "Unreadable.", 0, 0),
            new WorkspaceDiagnostic("topics/one.mcs.yml", "Unreadable.", 0, 0),
        ]);

        Assert.Equal(2, failure.Diagnostics.Length);
        Assert.StartsWith("2 workspace files could not be read:", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 1, WorkspaceDiagnosticKind.InvalidFile)]
    [InlineData(2, 2, WorkspaceDiagnosticKind.InvalidFile)]
    [InlineData(2, 1, WorkspaceDiagnosticKind.MergeConflict)]
    public void WorkspaceValidationException_DifferentPositionOrKind_RemainsDistinct(int line, int column, WorkspaceDiagnosticKind kind)
    {
        var failure = new WorkspaceValidationException(
        [
            new WorkspaceDiagnostic("topics/One.mcs.yml", "Problem.", 2, 1),
            new WorkspaceDiagnostic("topics/One.mcs.yml", "Problem.", line, column, kind),
        ]);

        Assert.Equal(2, failure.Diagnostics.Length);
        Assert.StartsWith("1 workspace file could not be read:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceDiagnostic_SummarizeWithExplicitHeader_DeduplicatesBeforeFormatting()
    {
        var diagnostic = new WorkspaceDiagnostic("topics/One.mcs.yml", "Problem.", 2, 1);
        var formatCalls = 0;

        var summary = WorkspaceDiagnostic.Summarize("Custom header:", new[] { diagnostic, diagnostic }, item =>
        {
            formatCalls++;
            return item.ToString();
        });

        Assert.Equal(1, formatCalls);
        Assert.Equal("Custom header:" + Environment.NewLine + "  " + diagnostic, summary);
    }

    [Fact]
    public void DiagnosticsCollector_DeduplicatesAcrossAddsWithoutDroppingDistinctProblems()
    {
        var collector = new SyncDiagnosticsCollector();
        var first = new WorkspaceDiagnostic("topics/One.mcs.yml", "First problem.", 2, 1);
        var second = new WorkspaceDiagnostic("topics/One.mcs.yml", "Second problem.", 2, 1);
        collector.Add(first);
        collector.AddRange([new WorkspaceDiagnostic(first.FilePath, first.Message, first.Line, first.Column), second]);

        Assert.True(collector.HasDiagnostics);
        Assert.Equal(new[] { first, second }, collector.Diagnostics);
    }

    private static AgentFilePath FindBrokenComponentPath(InMemoryFileAccessor accessor) => accessor.ListFiles(filePattern: "*.mcs.yml").First(path => path.ToString().Contains("Broken", StringComparison.Ordinal));

    [Fact]
    public async Task ReadWorkspaceDefinition_ClonedWorkspace_ProjectsComponentAndDoesNotThrow()
    {
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync();

        Assert.NotNull(await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false));
        Assert.Contains("Broken", FindBrokenComponentPath(accessor).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n", "Duplicate key")]
    [InlineData("kind: AdaptiveDialog\nbeginDialog:\n\tkind: OnRecognizedIntent\n", "Tab")]
    [InlineData("kind: AdaptiveDialog\ndisplayName: \"unterminated\n", "quoted")]
    [InlineData("kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n", "Unresolved merge conflict")]
    public async Task ReadWorkspaceDefinition_InvalidComponentFile_ThrowsNamingTheFile(string invalidYaml, string expectedMessageFragment)    {
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync();
        var brokenPath = FindBrokenComponentPath(accessor);
        await accessor.WriteAsync(brokenPath, invalidYaml, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal(brokenPath.ToString(), diagnostic.FilePath);
        Assert.Contains(expectedMessageFragment, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(brokenPath.ToString(), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_InvalidComponentFile_ReportsPosition()
    {
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync();
        await accessor.WriteAsync(FindBrokenComponentPath(accessor), "kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n", CancellationToken.None);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false));

        Assert.True(failure.Diagnostics.Single().Line > 0);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_InvalidComponentFile_DoesNotSubstituteTheCloudCache()
    {
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync();
        await accessor.WriteAsync(FindBrokenComponentPath(accessor), "kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n", CancellationToken.None);

        await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false));
    }

    [Theory]
    [InlineData("kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n", WorkspaceDiagnosticKind.InvalidFile)]
    [InlineData("kind: AdaptiveDialog\nbeginDialog:\n\tkind: OnRecognizedIntent\n", WorkspaceDiagnosticKind.InvalidFile)]
    [InlineData("kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n", WorkspaceDiagnosticKind.MergeConflict)]
    public async Task ReadWorkspaceDefinition_InvalidComponentFile_ClassifiesTheDiagnostic(string invalidYaml, WorkspaceDiagnosticKind expectedKind)
    {
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync();
        await accessor.WriteAsync(FindBrokenComponentPath(accessor), invalidYaml, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(
            () => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false));

        Assert.Equal(expectedKind, failure.Diagnostics.Single().Kind);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ReadWorkspaceDefinition_InvalidSettings_UsesSharedDiagnostics(bool cached, bool classic, bool withMarker)
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        if (cached)
        {
            var entity = CodeSerializer.Deserialize<BotEntity>(classic ? "schemaName: cr123_gatetest\n" : CliSettings)!;
            WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition(entity: entity));
        }

        if (!withMarker)
        {
            accessor.Delete(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName));
        }

        foreach (var (yaml, kind) in new[]
        {
            ("displayName: one\ndisplayName: two\n", WorkspaceDiagnosticKind.InvalidFile),
            ("\n<<<<<<< ours\ndisplayName: one\n=======\ndisplayName: two\n>>>>>>> theirs\n", WorkspaceDiagnosticKind.MergeConflict),
        })
        {
            Write(accessor, "settings.mcs.yml", yaml);

            var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None));

            var diagnostic = Assert.Single(failure.Diagnostics);
            Assert.Equal("settings.mcs.yml", diagnostic.FilePath);
            Assert.Equal(2, diagnostic.Line);
            Assert.Equal(1, diagnostic.Column);
            Assert.Equal(kind, diagnostic.Kind);
        }
    }

    [Theory]
    [InlineData("settings.mcs.yml")]
    [InlineData("topics/New.mcs.yml")]
    public async Task ReadWorkspaceDefinition_InvalidNewFile_AggregatesWithExistingComponent(string newFile)
    {
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync();
        var brokenPath = FindBrokenComponentPath(accessor);
        Write(accessor, brokenPath.ToString(), "kind: AdaptiveDialog\nkind: AdaptiveDialog\n");
        Write(accessor, newFile, "displayName: one\ndisplayName: two\n");

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None));

        Assert.Equal(2, failure.Diagnostics.Length);
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.FilePath == newFile && diagnostic.Line == 2);
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.FilePath == brokenPath.ToString() && diagnostic.Line == 2);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_CachedInvalidSettings_CollectsWithoutChangingCache()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var original = new BotDefinition(entity: CodeSerializer.Deserialize<BotEntity>(CliSettings)!);
        WorkspaceSynchronizer.WriteCloudCache(accessor, original);
        var cachePath = new AgentFilePath(".mcs/botdefinition.json");
        var originalCache = await accessor.ReadStringAsync(cachePath, CancellationToken.None);
        var settings = "displayName: one\ndisplayName: two\n";
        Write(accessor, "settings.mcs.yml", settings);
        var diagnostics = new SyncDiagnosticsCollector();

        await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: false, diagnostics, validate: false);

        Assert.Equal("settings.mcs.yml", Assert.Single(diagnostics.Diagnostics).FilePath);
        Assert.Equal(originalCache, await accessor.ReadStringAsync(cachePath, CancellationToken.None));
        Assert.Equal(settings, await accessor.ReadStringAsync(new AgentFilePath("settings.mcs.yml"), CancellationToken.None));
    }

    [Fact]
    public void WorkspaceDiagnostic_WrappedYamlFailure_PreservesPositionAndKind()
    {
        var failure = new InvalidOperationException("outer", new McsYamlFormatException("conflict", 12, 4, McsYamlError.MergeConflict));

        var diagnostic = WorkspaceDiagnostic.FromException("topics/Broken.mcs.yml", failure);

        Assert.Equal("topics/Broken.mcs.yml", diagnostic.FilePath);
        Assert.Equal("conflict", diagnostic.Message);
        Assert.Equal(12, diagnostic.Line);
        Assert.Equal(4, diagnostic.Column);
        Assert.Equal(WorkspaceDiagnosticKind.MergeConflict, diagnostic.Kind);
    }

    [Fact]
    public void WorkspaceDiagnostic_UnpositionedFailure_DoesNotInventPosition()
    {
        var diagnostic = WorkspaceDiagnostic.FromException("settings.mcs.yml", new IOException("Cannot read file."));

        Assert.Equal("Cannot read file.", diagnostic.Message);
        Assert.Equal(0, diagnostic.Line);
        Assert.Equal(0, diagnostic.Column);
        Assert.Equal(WorkspaceDiagnosticKind.InvalidFile, diagnostic.Kind);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_InvalidComponent_DoesNotReportDetailsToProgress()
    {
        var messages = new List<string>();
        var (sync, accessor, workspace) = await CreateClonedWorkspaceAsync(new TestSyncProgress(messages));
        var brokenPath = FindBrokenComponentPath(accessor);
        Write(accessor, brokenPath.ToString(), "kind: AdaptiveDialog\ncustomer-private-key: one\ncustomer-private-key: two\n");
        messages.Clear();

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None));

        Assert.Contains("customer-private-key", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(messages, message => message.Contains(brokenPath.ToString(), StringComparison.Ordinal) || message.Contains("customer-private-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_InvalidNewCliFile_UsesSharedDiagnostics()
    {
        var (sync, accessor, workspace) = await CreateWorkspaceAsync();
        var path = LspProjection.CliComponentBodyFolders.First() + "/Broken.mcs.yml";
        Write(accessor, path, "kind: AdaptiveDialog\nkind: AdaptiveDialog\n");

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal(path, diagnostic.FilePath);
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal(1, diagnostic.Column);
        Assert.Equal(WorkspaceDiagnosticKind.InvalidFile, diagnostic.Kind);
    }
}
