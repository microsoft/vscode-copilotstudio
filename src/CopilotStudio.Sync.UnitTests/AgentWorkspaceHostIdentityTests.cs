// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Hosting;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostIdentityTests
{
    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly AgentWorkspaceHost _host;

    public AgentWorkspaceHostIdentityTests() => _host = _cloud.CreateHost(_files);

    public static TheoryData<string> AuthoringModes => new() { AgentAuthoringModes.Classic, AgentAuthoringModes.CliCopilot };

    [Theory]
    [MemberData(nameof(AuthoringModes))]
    public async Task ReadIdentityAsync_OfAScaffold_ReadsTheNamesItDeclaresWithoutTouchingAnyEnvironment(string authoringMode)
    {
        var identity = await _host.ReadIdentityAsync(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, authoringMode), CancellationToken.None);

        Assert.Equal((SchemaName, "Contoso Helpdesk"), (identity!.SchemaName, identity.DisplayName));
        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task ReadIdentityAsync_OfAClassicAgentWithCollections_ReadsTheAgentFolder()
    {
        Assert.Equal(new AgentWorkspaceIdentity(ClassicAgentFixtures.Cc1Schema, "S5D2C1", null) { DeclaredDisplayName = "S5D2C1" }, await _host.ReadIdentityAsync(ClassicAgentFixtures.Cc1(), CancellationToken.None));
    }

    [Theory]
    [InlineData("displayName: Contoso Helpdesk\n")]
    [InlineData("schemaName: \"unclosed\n")]
    public async Task ReadIdentityAsync_OfAnAgentWhoseSettingsNameNoReadableSchema_ReturnsNull(string settings)
    {
        Assert.Null(await _host.ReadIdentityAsync(With(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic), "Agent/settings.mcs.yml", settings), CancellationToken.None));
    }

    [Fact]
    public async Task ReadIdentityAsync_OfAStandaloneWorkflow_RefusesTheSnapshotAsHoldingNoAgent()
    {
        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.ReadIdentityAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.SnapshotInvalid, failure.Code);
        Assert.Equal((AgentSyncDiagnosticCodes.AgentProjectMissing, "Weather/settings.mcs.yml"), (Assert.Single(failure.Diagnostics).Code, failure.Diagnostics[0].Path));
    }

    [Fact]
    public async Task ReadIdentityAsync_OfASnapshotThatBreaksALayoutRule_RefusesItWithTheValidationFindings()
    {
        var snapshot = With(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic), "Stray/notes.txt", "stray");

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.ReadIdentityAsync(snapshot, CancellationToken.None));

        Assert.Equal((AgentWorkspaceHostErrorCodes.SnapshotInvalid, AgentSyncDiagnosticCodes.OutsideDeclaredFolders), (failure.Code, Assert.Single(failure.Diagnostics).Code));
    }

    [Fact]
    public async Task ReadIdentityAsync_WhenCanceledOrGivenNoSnapshot_ThrowsBeforeReading()
    {
        var snapshot = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.ReadIdentityAsync(snapshot, new CancellationToken(canceled: true)));
        Assert.Equal("snapshot", (await Assert.ThrowsAsync<ArgumentNullException>(() => _host.ReadIdentityAsync(null!, CancellationToken.None))).ParamName);
    }

    [Fact]
    public async Task ReadIdentityAsync_OfAnAgentDeclaringNoDisplayName_FallsBackToTheSchemaNameButDeclaresNone()
    {
        var identity = await _host.ReadIdentityAsync(With(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic), "Agent/settings.mcs.yml", $"schemaName: {SchemaName}\nlanguage: 1033\n"), CancellationToken.None);

        Assert.Equal((SchemaName, (string?)null), (identity!.DisplayName, identity.DeclaredDisplayName));
    }

    [Fact]
    public async Task ReadWorkflowIdentityAsync_OfAScaffold_ReadsTheNameItsMetadataDeclaresWithoutTouchingAnyEnvironment()
    {
        Assert.Equal(new WorkflowWorkspaceIdentity("Weather"), await _host.ReadWorkflowIdentityAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", Guid.NewGuid()), CancellationToken.None));
        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData("workflowId: $id\nname: \"\"\n")]
    [InlineData("workflowId: $id\nname: \"   \"\n")]
    [InlineData("workflowId: $id\n")]
    [InlineData("workflowId: $other\nname: Weather\n")]
    [InlineData("workflowId: $id\nname: Weather\nunknownKey: 1\n")]
    [InlineData("workflowId: $id\nname: [unclosed\n")]
    [InlineData("")]
    public async Task ReadWorkflowIdentityAsync_OfMetadataThatNamesNoUsableWorkflow_ReturnsNull(string metadata)
    {
        var workflowId = Guid.NewGuid();

        Assert.Null(await _host.ReadWorkflowIdentityAsync(With(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", workflowId), MetadataPath(workflowId), metadata.Replace("$id", workflowId.ToString(), StringComparison.Ordinal).Replace("$other", Guid.NewGuid().ToString(), StringComparison.Ordinal)), CancellationToken.None));
    }

    [Theory]
    [InlineData("no metadata", AgentSyncDiagnosticCodes.WorkflowProjectInvalid)]
    [InlineData("two workflows", AgentSyncDiagnosticCodes.WorkflowProjectInvalid)]
    [InlineData("an agent", AgentSyncDiagnosticCodes.WorkflowProjectInvalid)]
    [InlineData("a stray folder", AgentSyncDiagnosticCodes.OutsideDeclaredFolders)]
    public async Task ReadWorkflowIdentityAsync_OfASnapshotThatBreaksAWorkflowRule_RefusesItWithTheValidationFinding(string shape, string code)
    {
        var workflowId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var snapshot = shape switch
        {
            "no metadata" => With(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", workflowId), MetadataPath(workflowId), null),
            "two workflows" => new AgentWorkspaceSnapshot("Weather", AgentWorkspaceHostWorkflowTests.Scaffold("Weather", workflowId).Files.Concat(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", other).Files.Where(file => file.Key.Contains(other.ToString(), StringComparison.OrdinalIgnoreCase))).ToDictionary(file => file.Key, file => file.Value)),
            "an agent" => Scaffolds.Agent("Weather", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic),
            _ => With(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", workflowId), "Stray/notes.txt", "stray"),
        };

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.ReadWorkflowIdentityAsync(snapshot, CancellationToken.None));

        Assert.Equal((AgentWorkspaceHostErrorCodes.SnapshotInvalid, code), (failure.Code, Assert.Single(failure.Diagnostics).Code));
    }

    [Fact]
    public async Task ReadWorkflowIdentityAsync_WhenCanceledOrGivenNoSnapshot_ThrowsBeforeReading()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.ReadWorkflowIdentityAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", Guid.NewGuid()), new CancellationToken(canceled: true)));
        Assert.Equal("snapshot", (await Assert.ThrowsAsync<ArgumentNullException>(() => _host.ReadWorkflowIdentityAsync(null!, CancellationToken.None))).ParamName);
    }

    private static string MetadataPath(Guid workflowId) => $"Weather/{WorkflowWorkspace.RelativeFolderFor("Weather", workflowId)}/{WorkflowWorkspace.MetadataFileName}";
}
