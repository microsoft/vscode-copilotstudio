using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CopilotStudio.Sync.Copy;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class WorkspaceCopyPreparationTests
{
    private const string CapturedElsewhereInternalId = "shared_cr1-5fweather-20plus-5fc57fa9bfea132312";

    private const string SecondConnectorInternalId = "shared_cr1-5fmaps-5fc57fa9bfea132312";

    private const string DuplicateConnectorName = "cr1_5Fweather-20plusa7d036a8";

    private const string ClassicConnectorReference = CopyTestWorkspace.ClassicSchema + "." + CapturedElsewhereInternalId + ".0b1c2d3e4f5a46b7c8d9e0f1a2b3c4d5";

    private const string ClassicConnectorToolFile = "actions/WeatherPlus-WeatherPlus.mcs.yml";

    private static readonly Guid DuplicateConnectorId = Guid.Parse("8b261c24-156b-425c-ae48-4b40e060cd27");

    private static readonly Guid OtherWorkflowId = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");

    public static TheoryData<string, string?, string?> NewWorkflowNames => new()
    {
        { new string('n', WorkflowNaming.MaximumLength + 1), CopyAuditCodes.WorkflowNameUnsupported, "The new workflow's name cannot be used: A workflow name can hold at most 100 characters, and this one holds 101." },
        { "Approve\texpenses", CopyAuditCodes.WorkflowNameUnsupported, "The new workflow's name cannot be used: A workflow name cannot hold control characters such as tabs or line breaks." },
        { "APPROVE EXPENSES", CopyAuditCodes.IdentityTaken, "The new name 'APPROVE EXPENSES' is the source workflow's own name." },
        { new string('n', WorkflowNaming.MaximumLength), null, null },
    };

    public static TheoryData<string, string, string?> MetadataValuesTheCopyCannotRewrite => new()
    {
        { "\nname: Approve expenses\n", "\nname: >-\n  Approve expenses\n", "The copy is still named 'Approve expenses', not 'Approve expenses copy': the source's metadata.yml writes the name in a form the copy cannot rewrite. Write it on one line and copy again." },
        { "\nstateCode: 1\n", "\nstateCode: >-\n  1\n", "The copy is not a draft (stateCode 0, statusCode 1): the source's metadata.yml writes the state in a form the copy cannot rewrite. Write it on one line and copy again." },
        { "\nstatusCode: 2\n", "\nstatusCode: |-\n  2\n", "The copy is not a draft (stateCode 0, statusCode 1): the source's metadata.yml writes the state in a form the copy cannot rewrite. Write it on one line and copy again." },
        { "\nstateCode: 1\nstatusCode: 2\n", "\nstateCode: >-\n  0\nstatusCode: |-\n  1\n", null },
        { "\nstateCode: 1\nstatusCode: 2\n", "\n", null },
    };

    public static TheoryData<string> SchemaNamesAnAgentCopyCannotTake => new() { "../../escaped", "cr1_copy/escaped", "cr1_copy\\escaped", "cr1 copy", string.Empty, new string('x', SchemaNameValidator.MaximumLength + 1) };

    public static TheoryData<bool, string, string, bool> WorkflowStatesAnAgentCopyCannotRewrite => new()
    {
        { true, "\nstateCode: 1\n", "\nstateCode: |-\n  1\n", true },
        { true, "\nstatusCode: 2\n", "\nstatusCode: >-\n  2\n", true },
        { false, "\nstateCode: 1\n", "\nstateCode: |-\n  1\n", true },
        { false, "\nstatusCode: 2\n", "\nstatusCode: >-\n  2\n", true },
        { true, "\nstateCode: 1\nstatusCode: 2\n", "\nstateCode: >-\n  0\nstatusCode: |-\n  1\n", false },
    };

    [Fact]
    public async Task Prepare_ClassicCopy_RewritesOwnedIdentitiesAndCopiesDependencies()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var request = workspace.Request();
        var destination = request.SchemaName;

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);

        var workflowCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.ClassicWorkflowId.ToString("D")));
        var modelCopy = Guid.Parse(Destination(result, CopyIdentityKinds.AIModel, CopyTestWorkspace.PromptModelId.ToString("D")));
        var teamsCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ClassicTeamsReference);
        var weatherCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ClassicWeatherReference);
        Assert.True(result.Audit.Passed);
        Assert.Equal(destination + ".shared_teams.0add195409434636943683365d5bbcce", teamsCopy);
        Assert.Matches("^new_sharedmsnweather_[0-9a-f]{8}$", weatherCopy);
        Assert.NotEmpty(result.Audit.ComponentSchemaNames);
        Assert.All(result.Audit.ComponentSchemaNames, schema => Assert.StartsWith(destination + ".", schema, StringComparison.Ordinal));

        var settings = workspace.Read("settings.mcs.yml");
        Assert.Contains($"schemaName: {destination}\n", settings, StringComparison.Ordinal);
        Assert.Contains($"displayName: {CopyTestWorkspace.DestinationDisplayName}\n", settings, StringComparison.Ordinal);
        Assert.Contains($"defaultSchemaName: {destination}.gpt.default\n", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("publishedOn", settings, StringComparison.Ordinal);
        Assert.Contains("instructions: Help employees with cr2_classic questions.", workspace.Read("agent.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("activity: Welcome to cr2_classic support.", workspace.Read("topics/Greeting.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains($"flowId: {workflowCopy:D}", workspace.Read("actions/ClassicFlow.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains($"aIModelId: {modelCopy:D}", workspace.Read("actions/promptone.mcs.yml"), StringComparison.Ordinal);
        var teamsAction = workspace.Read("actions/MicrosoftTeams-Createachat.mcs.yml");
        Assert.Contains($"connectionReference: '{teamsCopy}'", teamsAction, StringComparison.Ordinal);
        Assert.Contains("propertyName: @odata.etag", teamsAction, StringComparison.Ordinal);
        Assert.False(workspace.Exists(CopyTestWorkspace.ClassicKnowledgeFile));
        Assert.True(workspace.Exists($"knowledge/{destination}.topic.Handbook_abc123.mcs.yml"));

        var declarations = workspace.Read("connectionreferences.mcs.yml");
        Assert.True(workspace.HasPreamble("connectionreferences.mcs.yml"));
        Assert.Contains(teamsCopy, declarations, StringComparison.Ordinal);
        Assert.Contains(weatherCopy, declarations, StringComparison.Ordinal);
        Assert.DoesNotContain(CopyTestWorkspace.ClassicTeamsReference, declarations, StringComparison.Ordinal);
        Assert.DoesNotContain(CopyTestWorkspace.ClassicWeatherReference, declarations, StringComparison.Ordinal);

        Assert.False(workspace.Exists(CopyTestWorkspace.ClassicWorkflowFolder));
        var workflowFolder = "workflows/" + workspace.Folder("workflows", workflowCopy);
        var metadata = workspace.Read(workflowFolder + "/metadata.yml");
        Assert.True(workspace.HasPreamble(workflowFolder + "/metadata.yml"));
        Assert.Contains($"jsonFileName: {workflowFolder}/workflow.json\n", metadata, StringComparison.Ordinal);
        Assert.Contains($"workflowId: {workflowCopy:D}\n", metadata, StringComparison.Ordinal);
        Assert.Contains("name: Team Copy - Classic Flow\n", metadata, StringComparison.Ordinal);
        Assert.Contains("stateCode: 0\n", metadata, StringComparison.Ordinal);
        Assert.Contains("statusCode: 1\n", metadata, StringComparison.Ordinal);
        Assert.Contains($"- {teamsCopy}\n", metadata, StringComparison.Ordinal);
        Assert.Contains($"- {weatherCopy}\n", metadata, StringComparison.Ordinal);
        var definition = workspace.Read(workflowFolder + "/workflow.json");
        Assert.True(workspace.HasPreamble(workflowFolder + "/workflow.json"));
        Assert.Contains($"\"connectionReferenceLogicalName\": \"{teamsCopy}\"", definition, StringComparison.Ordinal);
        Assert.Contains($"\"connectionReferenceLogicalName\": \"{weatherCopy}\"", definition, StringComparison.Ordinal);
        Assert.Equal(new[] { "Team Copy - Classic Flow" }, result.DraftWorkflows);

        Assert.False(workspace.Exists(CopyTestWorkspace.PromptFolder));
        var promptFolder = "prompts/" + workspace.Folder("prompts", modelCopy);
        Assert.Contains($"aIModelId: {modelCopy:D}", workspace.Read(promptFolder + "/metadata.yml"), StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Team Copy - prompt one\"", workspace.Read(promptFolder + "/prompt.json"), StringComparison.Ordinal);

        Assert.Contains(new CopyIdentityMapping(CopyIdentityKinds.Schema, CopyTestWorkspace.ClassicSchema, destination), result.Identities);
        Assert.Equal(new[] { CopyIdentityKinds.Schema, CopyIdentityKinds.Workflow, CopyIdentityKinds.AIModel, CopyIdentityKinds.ConnectionReference }, result.Identities.Select(mapping => mapping.Kind).Distinct());
        Assert.False(workspace.Exists("copy.sync.yaml"));
    }

    [Fact]
    public async Task Prepare_CopyOfAFlowThatRunsACopiedPrompt_RunsThePromptsCopy()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.AddPromptRun(CopyTestWorkspace.ClassicWorkflowFolder, CopyTestWorkspace.PromptModelId);

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var modelCopy = Destination(result, CopyIdentityKinds.AIModel, CopyTestWorkspace.PromptModelId.ToString("D"));
        var definition = workspace.Read("workflows/" + workspace.Folder("workflows", Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.ClassicWorkflowId.ToString("D")))) + "/workflow.json");
        Assert.True(result.Audit.Passed);
        Assert.Contains($"\"recordId\": \"{modelCopy}\"", definition, StringComparison.Ordinal);
        Assert.DoesNotContain(CopyTestWorkspace.PromptModelId.ToString("D"), definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prepare_AgentCopyOfAFlow_RemapsOnlyTheFlowsAndPromptsItsActionsCall()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var promptId = CopyTestWorkspace.PromptModelId.ToString("D");
        var workflowId = CopyTestWorkspace.ClassicWorkflowId.ToString("D");
        workspace.AddAction(CopyTestWorkspace.ClassicWorkflowFolder, $"\"Run_a_prompt\": {{ \"type\": \"OpenApiConnection\", \"inputs\": {{ \"host\": {{ \"operationId\": \"aibuilderpredict_customprompt\" }}, \"parameters\": {{ \"recordId\": \"{promptId}\" }} }} }},\n        \"Retry\": {{ \"type\": \"Scope\", \"actions\": {{ \"Run_again\": {{ \"type\": \"Workflow\", \"inputs\": {{ \"host\": {{ \"workflowReferenceName\": \"{workflowId}\" }} }} }} }} }},\n        \"Compose\": {{ \"type\": \"Compose\", \"inputs\": \"{promptId}\" }},\n        \"HTTP\": {{ \"type\": \"Http\", \"inputs\": {{ \"method\": \"POST\", \"uri\": \"https://example.com/flows\", \"body\": {{ \"workflowReferenceName\": \"{workflowId}\", \"recordId\": \"{promptId}\" }} }} }}");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        using var definition = JsonDocument.Parse(workspace.Read(workspace.CopiedWorkflowFolder(result) + "/workflow.json"));
        var actions = definition.RootElement.GetProperty("properties").GetProperty("definition").GetProperty("actions");
        var body = actions.GetProperty("HTTP").GetProperty("inputs").GetProperty("body");
        Assert.True(result.Audit.Passed);
        Assert.Equal(Destination(result, CopyIdentityKinds.AIModel, promptId), actions.GetProperty("Run_a_prompt").GetProperty("inputs").GetProperty("parameters").GetProperty("recordId").GetString());
        Assert.Equal(Destination(result, CopyIdentityKinds.Workflow, workflowId), actions.GetProperty("Retry").GetProperty("actions").GetProperty("Run_again").GetProperty("inputs").GetProperty("host").GetProperty("workflowReferenceName").GetString());
        Assert.Equal(promptId, actions.GetProperty("Compose").GetProperty("inputs").GetString());
        Assert.Equal(workflowId, body.GetProperty("workflowReferenceName").GetString());
        Assert.Equal(promptId, body.GetProperty("recordId").GetString());
    }

    [Fact]
    public async Task Prepare_ClassicCopy_RenamesTheAgentAndEveryToolStillNamedAfterTheFlowOrPromptItCalls()
    {
        using var workspace = CopyTestWorkspace.Classic();

        await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var promptTool = workspace.Read("actions/promptone.mcs.yml");
        var connectorTool = workspace.Read("actions/MicrosoftTeams-Createachat.mcs.yml");
        Assert.Contains("componentName: Team Copy\n", workspace.Read("agent.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("componentName: Team Copy - Classic Flow\n", workspace.Read("actions/ClassicFlow.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("componentName: Team Copy - prompt one\n", promptTool, StringComparison.Ordinal);
        Assert.Contains("modelDisplayName: Team Copy - prompt one\n", promptTool, StringComparison.Ordinal);
        Assert.Contains("componentName: Microsoft Teams - Create a chat\n", connectorTool, StringComparison.Ordinal);
        Assert.Contains("modelDisplayName: Create a chat\n", connectorTool, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_CliCopy_RenamesTheWorkflowToolStillNamedAfterItsFlow()
    {
        using var workspace = CopyTestWorkspace.Cli();

        await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.Contains("componentName: Team Copy - Cli Flow\n", workspace.Read("capabilities/tools/CliFlow_kvb.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("componentName: Get a section\n", workspace.Read(CopyTestWorkspace.TeamsToolFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_Copy_KeepsEveryNameThatNoLongerMatchesTheFlowOrPromptItCalls()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write("actions/ClassicFlow.mcs.yml", "mcs.metadata:\n  componentName: Escalate to a person\n  description: Classic Flow\nkind: TaskDialog\naction:\n  kind: InvokeFlowTaskAction\n  flowId: 94945b98-fe8c-f111-8077-7ced8d702753\n\noutputMode: All\n");
        workspace.Write("actions/promptone.mcs.yml", "mcs.metadata:\n  componentName: Summarize\nkind: TaskDialog\nmodelDisplayName: prompt one\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\n\noutputMode: All\n");

        await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var flowTool = workspace.Read("actions/ClassicFlow.mcs.yml");
        var promptTool = workspace.Read("actions/promptone.mcs.yml");
        Assert.Contains("componentName: Escalate to a person\n", flowTool, StringComparison.Ordinal);
        Assert.Contains("description: Classic Flow\n", flowTool, StringComparison.Ordinal);
        Assert.Contains("componentName: Summarize\n", promptTool, StringComparison.Ordinal);
        Assert.Contains("modelDisplayName: Team Copy - prompt one\n", promptTool, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Prepare_CliCopy_RenamesLayeredDeclarationsAndCopiesTheWorkflow()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var request = workspace.Request();
        var destination = request.SchemaName;

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);

        var workflowCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.CliWorkflowId.ToString("D")));
        var teamsCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.CliTeamsReference);
        var weatherCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.CliWeatherReference);
        Assert.True(result.Audit.Passed);
        Assert.Equal(destination + ".cr.shared_teams", teamsCopy);
        Assert.Matches("^new_sharedmsnweather_[0-9a-f]{8}$", weatherCopy);
        Assert.NotEqual(CopyTestWorkspace.CliWeatherReference, weatherCopy);
        Assert.Contains($"workflowId: {workflowCopy:D}", workspace.Read("capabilities/tools/CliFlow_kvb.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains($"connectionReference: \"{teamsCopy}\"", workspace.Read(CopyTestWorkspace.TeamsToolFile), StringComparison.Ordinal);
        Assert.False(workspace.Exists("infrastructure/connections/cr1_cli.cr.shared_teams.sync.yaml"));
        Assert.False(workspace.Exists("infrastructure/connections/new_sharedmsnweather_1234abcd.sync.yaml"));
        Assert.Contains($"connectionReferenceLogicalName: {teamsCopy}", workspace.Read($"infrastructure/connections/{teamsCopy}.sync.yaml"), StringComparison.Ordinal);
        Assert.Contains($"connectionReferenceLogicalName: {weatherCopy}", workspace.Read($"infrastructure/connections/{weatherCopy}.sync.yaml"), StringComparison.Ordinal);
        Assert.False(workspace.Exists("connectionreferences.mcs.yml"));
        Assert.True(workspace.Exists($"capabilities/knowledge/{destination}.Handbook_xyz789.mcs.yml"));
        Assert.Equal("layoutVersion: 1\n", workspace.Read("agent.sync.yaml"));
        var workflowFolder = "workflows/" + workspace.Folder("workflows", workflowCopy);
        Assert.Contains($"\"connectionReferenceLogicalName\": \"{teamsCopy}\"", workspace.Read(workflowFolder + "/workflow.json"), StringComparison.Ordinal);
        Assert.Contains(result.RenamedPaths, renamed => renamed.Source == CopyTestWorkspace.CliWorkflowFolder && renamed.Destination == workflowFolder);
    }
    [Fact]
    public async Task Prepare_CopyWithAPromptMissingItsDefinition_IsBlockedBeforeAnythingIsRewritten()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Delete(CopyTestWorkspace.PromptFolder + "/prompt.json");
        var originalSettings = workspace.Read("settings.mcs.yml");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.UnsupportedResource);
        Assert.Equal(originalSettings, workspace.Read("settings.mcs.yml"));
    }
    [Fact]
    public async Task Prepare_CopyCallingAWorkflowTheWorkspaceDoesNotHold_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write("actions/Missing.mcs.yml", "mcs.metadata:\n  componentName: Missing\nkind: TaskDialog\naction:\n  kind: InvokeFlowTaskAction\n  flowId: 11111111-2222-3333-4444-555555555555\n\noutputMode: All\n");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.UnresolvedReference && finding.Message.Contains("11111111-2222-3333-4444-555555555555", StringComparison.Ordinal) && finding.Message.EndsWith("Pull the source again.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_CopyCallingAnAIPromptTheWorkspaceDoesNotHold_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write("actions/MissingPrompt.mcs.yml", "mcs.metadata:\n  componentName: MissingPrompt\nkind: TaskDialog\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: 99999999-2222-3333-4444-555555555555\n\noutputMode: All\n");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.UnresolvedReference && finding.Message.Contains("AI prompt 99999999-2222-3333-4444-555555555555", StringComparison.Ordinal) && finding.Message.EndsWith("Pull the source again.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public async Task Prepare_WithUnparseableConnectorMetadata_IsBlocked(string metadata)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("connectors/broken/metadata.yml", metadata);

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.Unreadable && finding.Path == "connectors/broken/metadata.yml" && finding.Message.StartsWith("The custom connector metadata could not be parsed (", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_AgentCopyOfAConnectorAPushReads_GivesItTheCopysIdentity(bool metadataNamesTheConnector)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector();
        workspace.Write(CopyTestWorkspace.ConnectorFolder + "/metadata.yml", "{\n" + (metadataNamesTheConnector ? $"  \"ConnectorId\": \"{CopyTestWorkspace.ConnectorId:D}\",\n" : string.Empty) + $"  \"Name\": \"{CopyTestWorkspace.ConnectorName}\",\n  \"DisplayName\": \"Weather Plus\",\n  \"ConnectorInternalId\": \"{CopyTestWorkspace.ConnectorInternalId}\",\n  \"OpenApiDefinition\": \"{CopyTestWorkspace.ConnectorFolder}/openapidefinition.json\",\n  \"ConnectionParameters\": \"{CopyTestWorkspace.ConnectorFolder}/connectionparameters.json\",\n  \"ConnectorType\": 1\n}}");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var connectorCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Connector, CopyTestWorkspace.ConnectorId.ToString("D")));
        var folder = workspace.Folder("connectors", connectorCopy);
        var pushed = WorkspaceSynchronizer.ParseLocalConnector(workspace.Read($"connectors/{folder}/metadata.yml"), folder);
        Assert.True(result.Audit.Passed);
        Assert.Equal(connectorCopy, pushed?.ConnectorId);
        Assert.Equal(Destination(result, CopyIdentityKinds.ConnectorInternalId, CopyTestWorkspace.ConnectorInternalId), pushed?.ConnectorInternalId);
        Assert.Equal($"connectors/{folder}/openapidefinition.json", pushed?.OpenApiDefinition);
        Assert.Empty(Mentioning(workspace, CopyTestWorkspace.ConnectorId.ToString("D")));
    }

    [Theory]
    [InlineData(CopyIdentityKinds.Workflow)]
    [InlineData(CopyIdentityKinds.AIModel)]
    [InlineData(CopyIdentityKinds.Connector)]
    public async Task Prepare_AgentCopyOfTwoFoldersHoldingOneIdentity_IsRefusedBeforeAnythingChanges(string kind)
    {
        using var workspace = kind == CopyIdentityKinds.Connector ? CopyTestWorkspace.Cli() : CopyTestWorkspace.Classic();
        string label;
        Guid identity;
        string[] folders;

        if (kind == CopyIdentityKinds.Workflow)
        {
            (label, identity) = ("workflow", CopyTestWorkspace.ClassicWorkflowId);
            folders = new[] { CopyTestWorkspace.ClassicWorkflowFolder, $"workflows/ClassicFlowAgain-{identity:D}" };
            workspace.WriteWorkflow(folders[1], identity, "Classic Flow Again", CopyTestWorkspace.ClassicTeamsReference, CopyTestWorkspace.ClassicWeatherReference);
        }
        else if (kind == CopyIdentityKinds.AIModel)
        {
            (label, identity) = ("AI prompt", CopyTestWorkspace.PromptModelId);
            folders = new[] { CopyTestWorkspace.PromptFolder, $"prompts/prompttwo-{identity:D}" };
            workspace.Write(folders[1] + "/metadata.yml", $"aIModelId: {identity:D}\nname: prompt two\n");
            workspace.Write(folders[1] + "/prompt.json", "{\n  \"name\": \"prompt two\"\n}\n");
        }
        else
        {
            (label, identity) = ("custom connector", CopyTestWorkspace.ConnectorId);
            folders = new[] { $"connectors/cr1_5Fmaps-{identity:D}", CopyTestWorkspace.ConnectorFolder };
            workspace.WriteConnector();
            workspace.WriteConnectorFolder(folders[0], identity, "cr1_5Fmaps", "Maps", SecondConnectorInternalId, "maps.example.com", version: null);
        }

        var before = workspace.Snapshot();

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The workspace holds the {label} {identity:D} in 2 folders ({folders[0]}, {folders[1]}), so a copy cannot give each its own identity. Keep one of them and copy again.", folders[0]), failure.Findings);
        Assert.Equal(before, workspace.Snapshot());
    }

    [Fact]
    public async Task Prepare_IntoTheSourceSchema_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(destinationSchema: CopyTestWorkspace.CliSchema), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.IdentityTaken);
    }

    [Fact]
    public async Task Prepare_AWorkspaceDescribingAnotherAgent_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { SourceSchemaName = "cr1_other" }, CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.Unreadable && finding.Path == "settings.mcs.yml");
    }

    [Fact]
    public async Task Prepare_AFolderWithoutSettings_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Delete("settings.mcs.yml");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.Unreadable && finding.Path == "settings.mcs.yml");
    }

    [Fact]
    public async Task Prepare_ASourceHoldingAnUnsupportedResourceKind_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var definitionPath = Path.Combine(Path.GetTempPath(), "copydefinition-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(definitionPath, "{\"$kind\":\"BotDefinition\",\"components\":[{\"$kind\":\"DialogComponent\"}],\"dataverseTableSearches\":[{\"id\":\"x\"}],\"flows\":[]}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(sourceDefinitionPath: definitionPath), CancellationToken.None));

            var finding = Assert.Single(failure.Findings);
            Assert.Equal(CopyAuditCodes.UnsupportedResource, finding.Code);
            Assert.Contains("dataverseTableSearches", finding.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(definitionPath);
        }
    }

    [Fact]
    public async Task Prepare_AnUnreadableSourceDefinition_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var definitionPath = Path.Combine(Path.GetTempPath(), "copydefinition-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(definitionPath, "{\"components\":[");

        try
        {
            var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(sourceDefinitionPath: definitionPath), CancellationToken.None));

            Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.Unreadable);
        }
        finally
        {
            File.Delete(definitionPath);
        }
    }

    [Fact]
    public async Task Prepare_AReferenceWhoseCopyWouldExceedTheLogicalNameLimit_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var longReference = "cr1_cli.cr." + new string('x', 185);
        workspace.Write($"infrastructure/connections/{longReference}.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {longReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.SchemaTooLong && finding.Message.Contains(longReference, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_AReferenceLongerThanAComponentSchemaButWithinTheLogicalNameLimit_Succeeds()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var longReference = "cr1_cli.shared_cre98-5fweather-20agent-202-5f2c520b622e718fe6.c712ea8cc54c478cb088f5d8081b15d6";
        workspace.Write($"infrastructure/connections/{longReference}.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {longReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_cre98-5fweather-20agent-202-5f2c520b622e718fe6\n");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.True(Destination(result, CopyIdentityKinds.ConnectionReference, longReference).Length > 100);
    }

    [Fact]
    public async Task Prepare_AComponentFileWithMergeConflictMarkers_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("capabilities/tools/Conflicted.mcs.yml", "mcs.metadata:\n  componentName: Conflicted\nkind: ConnectorTool\n<<<<<<< HEAD\nconnectionReference: cr1_cli.cr.shared_teams\n=======\nconnectionReference: cr1_cli.cr.shared_other\n>>>>>>> branch\noperationId: GetSection\n");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.Unreadable && finding.Path == "capabilities/tools/Conflicted.mcs.yml");
    }

    [Fact]
    public async Task Prepare_HonorsCancellation()
    {
        using var workspace = CopyTestWorkspace.Classic();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), cancellation.Token));

    }

    [Fact]
    public async Task Prepare_CopyWithACustomConnector_CopiesItUnderTheInternalIdentifierTheEnvironmentWillAssign()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var connectorCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Connector, CopyTestWorkspace.ConnectorId.ToString("D")));
        var internalIdCopy = Destination(result, CopyIdentityKinds.ConnectorInternalId, CopyTestWorkspace.ConnectorInternalId);
        var referenceCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ConnectorReference);
        var predicted = Regex.Match(internalIdCopy, "^shared_cr1-5fweather-20plus-20(?<marker>[0-9a-f]{6})-5f566c5f3d9705a6dd$");
        var nameCopy = CopyTestWorkspace.ConnectorName + predicted.Groups["marker"].Value;
        var connectorFolder = $"connectors/{nameCopy}-{connectorCopy:D}";
        Assert.True(result.Audit.Passed);
        Assert.True(predicted.Success, internalIdCopy);
        Assert.Equal($"{CopyTestWorkspace.DestinationSchemaFor(CopyTestWorkspace.CliSchema)}.cr.{internalIdCopy}.0b1c2d3e4f5a46b7c8d9e0f1a2b3c4d5", referenceCopy);
        Assert.False(workspace.Exists(CopyTestWorkspace.ConnectorFolder));
        Assert.Equal(connectorFolder, "connectors/" + workspace.Folder("connectors", connectorCopy));
        Assert.Contains(result.RenamedPaths, renamed => renamed.Source == CopyTestWorkspace.ConnectorFolder && renamed.Destination == connectorFolder);

        using var metadata = JsonDocument.Parse(workspace.Read(connectorFolder + "/metadata.yml"));
        Assert.Equal(connectorCopy.ToString("D"), metadata.RootElement.GetProperty("connectorid").GetString());
        Assert.Equal(nameCopy, metadata.RootElement.GetProperty("name").GetString());
        Assert.Equal("Weather Plus " + predicted.Groups["marker"].Value, metadata.RootElement.GetProperty("displayname").GetString());
        Assert.Equal(internalIdCopy, metadata.RootElement.GetProperty("connectorinternalid").GetString());
        Assert.Equal(connectorFolder + "/openapidefinition.json", metadata.RootElement.GetProperty("openapidefinition").GetString());
        Assert.Equal(connectorFolder + "/connectionparameters.json", metadata.RootElement.GetProperty("connectionparameters").GetString());

        var tool = workspace.Read(CopyTestWorkspace.ConnectorToolFile);
        Assert.Contains($"connectionReference: \"{referenceCopy}\"\n", tool, StringComparison.Ordinal);
        Assert.Contains($"connectorId: /providers/Microsoft.PowerApps/apis/{internalIdCopy}\n", tool, StringComparison.Ordinal);
        Assert.False(workspace.Exists($"infrastructure/connections/{CopyTestWorkspace.ConnectorReference}.sync.yaml"));
        Assert.Contains($"connectorId: /providers/Microsoft.PowerApps/apis/{internalIdCopy}\n", workspace.Read($"infrastructure/connections/{referenceCopy}.sync.yaml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3139127L, 3036896L, DuplicateConnectorName, false)]
    [InlineData(3036896L, 3139127L, DuplicateConnectorName, true)]
    [InlineData(null, null, "cr1_5Fweather-20Plusa7d036a8", true)]
    public async Task Prepare_CopyWithCustomConnectorsSharingAnInternalId_CopiesOnlyTheOneSavedLastAndLeavesTheOthersOutWithoutANotice(long? version, long? duplicateVersion, string duplicateName, bool copiesTheDuplicate)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector(version: version);
        var duplicateFolder = WriteDuplicateConnector(workspace, duplicateName, duplicateVersion);
        var (copiedId, copiedName, copiedHost) = copiesTheDuplicate
            ? (DuplicateConnectorId, duplicateName, "stale.example.com")
            : (CopyTestWorkspace.ConnectorId, CopyTestWorkspace.ConnectorName, "weather.example.com");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var connectorMapping = Assert.Single(result.Identities, mapping => mapping.Kind == CopyIdentityKinds.Connector);
        var internalIdMapping = Assert.Single(result.Identities, mapping => mapping.Kind == CopyIdentityKinds.ConnectorInternalId);
        var referenceCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ConnectorReference);
        var connectorFolder = "connectors/" + workspace.Folder("connectors", Guid.Parse(connectorMapping.Destination));
        Assert.True(result.Audit.Passed);
        Assert.Equal(copiedId.ToString("D"), connectorMapping.Source);
        Assert.Equal(CopyTestWorkspace.ConnectorInternalId, internalIdMapping.Source);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(workspace.Root, "connectors")));
        Assert.False(workspace.Exists(CopyTestWorkspace.ConnectorFolder));
        Assert.False(workspace.Exists(duplicateFolder));

        using var metadata = JsonDocument.Parse(workspace.Read(connectorFolder + "/metadata.yml"));
        Assert.Matches("^" + Regex.Escape(copiedName) + "[0-9a-f]{6}$", metadata.RootElement.GetProperty("name").GetString());
        Assert.Equal(internalIdMapping.Destination, metadata.RootElement.GetProperty("connectorinternalid").GetString());
        Assert.Contains(copiedHost, workspace.Read(connectorFolder + "/openapidefinition.json"), StringComparison.Ordinal);
        Assert.Contains($"connectorId: /providers/Microsoft.PowerApps/apis/{internalIdMapping.Destination}\n", workspace.Read(CopyTestWorkspace.ConnectorToolFile), StringComparison.Ordinal);
        Assert.Contains($"connectorId: /providers/Microsoft.PowerApps/apis/{internalIdMapping.Destination}\n", workspace.Read($"infrastructure/connections/{referenceCopy}.sync.yaml"), StringComparison.Ordinal);
        Assert.Empty(result.Audit.Notices);
    }
    [Fact]
    public async Task Prepare_ClassicCopyWithAConnectorTool_NamesTheToolAndItsReferenceAfterTheCopiedConnector()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.WriteConnectorFolder(CopyTestWorkspace.ConnectorFolder, CopyTestWorkspace.ConnectorId, CopyTestWorkspace.ConnectorName, "Weather Plus", CopyTestWorkspace.ConnectorInternalId, "weather.example.com", null);
        workspace.Write("connectionreferences.mcs.yml", workspace.Read("connectionreferences.mcs.yml") + $"  - connectionReferenceLogicalName: {ClassicConnectorReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/{CopyTestWorkspace.ConnectorInternalId}\n", preamble: true);
        workspace.Write(ClassicConnectorToolFile, $"mcs.metadata:\n  componentName: Weather Plus - Weather Plus\nkind: TaskDialog\nmodelDisplayName: Weather Plus\naction:\n  kind: InvokeConnectorTaskAction\n  connectionReference: '{ClassicConnectorReference}'\n  operationId: SendActivity\n\noutputMode: All\n");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var referenceCopy = Destination(result, CopyIdentityKinds.ConnectionReference, ClassicConnectorReference);
        var tool = workspace.Read(ClassicConnectorToolFile);
        Assert.True(result.Audit.Passed);
        Assert.Equal($"{CopyTestWorkspace.DestinationSchemaFor(CopyTestWorkspace.ClassicSchema)}.{Destination(result, CopyIdentityKinds.ConnectorInternalId, CopyTestWorkspace.ConnectorInternalId)}.0b1c2d3e4f5a46b7c8d9e0f1a2b3c4d5", referenceCopy);
        Assert.Contains($"  componentName: {CopiedConnectorDisplayName(workspace, result)} - Weather Plus\n", tool, StringComparison.Ordinal);
        Assert.Contains("modelDisplayName: Weather Plus\n", tool, StringComparison.Ordinal);
        Assert.Contains($"connectionReference: '{referenceCopy}'", tool, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Weather Plus - Get forecast", true)]
    [InlineData("Get forecast", false)]
    [InlineData("Weather Plus", false)]
    [InlineData("Weather Plusses - Get forecast", false)]
    public async Task Prepare_WithAToolNamedAfterTheCustomConnectorItCalls_GivesItTheCopysNameOnlyWhenTheNameStillMatches(string toolName, bool renamed)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector();
        RenameTool(workspace, CopyTestWorkspace.ConnectorToolFile, "Get forecast", toolName);
        RenameTool(workspace, CopyTestWorkspace.TeamsToolFile, "Get a section", "Weather Plus - Get a section");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.True(result.Audit.Passed);
        Assert.Contains($"  componentName: {(renamed ? CopiedConnectorDisplayName(workspace, result) + " - Get forecast" : toolName)}\n", workspace.Read(CopyTestWorkspace.ConnectorToolFile), StringComparison.Ordinal);
        Assert.Contains("  componentName: Weather Plus - Get a section\n", workspace.Read(CopyTestWorkspace.TeamsToolFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_CopyWithALongToolNamedAfterItsCustomConnector_KeepsTheNewNameWithinOneHundredCharacters()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector();
        var operation = new string('a', 85);
        RenameTool(workspace, CopyTestWorkspace.ConnectorToolFile, "Get forecast", "Weather Plus - " + operation);

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.True(result.Audit.Passed);
        Assert.Contains($"  componentName: {(CopiedConnectorDisplayName(workspace, result) + " - " + operation).Substring(0, 100)}\n", workspace.Read(CopyTestWorkspace.ConnectorToolFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Weather Plus Forecast Service1", "shared_cr1-5fweather-20plus-20forecast-20service1-5f566c5f3d9705a6dd", "Weather Plus Forecast S")]
    [InlineData("Weather v2.1 (beta)", "shared_cr1-5fweather-20v2-2e1-20-28beta-29-5f566c5f3d9705a6dd", "Weather v2 1  beta")]
    [InlineData("_Weather-Plus_", "shared_cr1-5f-5fweather-2dplus-5f-5f566c5f3d9705a6dd", "Weather-Plus")]
    [InlineData("...", "shared_cr1-5f-2e-2e-2e-5f566c5f3d9705a6dd", "")]
    public async Task Prepare_CopyWithACustomConnector_GivesTheCopyADisplayNameTheEnvironmentAccepts(string displayName, string internalId, string expectedStem)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector(internalId, displayName);

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var connectorCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Connector, CopyTestWorkspace.ConnectorId.ToString("D")));
        var internalIdCopy = Destination(result, CopyIdentityKinds.ConnectorInternalId, internalId);
        using var metadata = JsonDocument.Parse(workspace.Read("connectors/" + workspace.Folder("connectors", connectorCopy) + "/metadata.yml"));
        var displayNameCopy = metadata.RootElement.GetProperty("displayname").GetString() ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.");
        var marker = (metadata.RootElement.GetProperty("name").GetString() ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).Substring(CopyTestWorkspace.ConnectorName.Length);
        var encodedStem = string.Concat(expectedStem.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character.ToString() : "-" + ((int)character).ToString("x2")));
        Assert.Matches("^[0-9a-f]{6}$", marker);
        Assert.Equal(expectedStem.Length == 0 ? marker : $"{expectedStem} {marker}", displayNameCopy);
        Assert.True(displayNameCopy.Length <= 30, displayNameCopy);
        Assert.Equal(expectedStem.Length == 0 ? $"shared_cr1-5f{marker}-5f566c5f3d9705a6dd" : $"shared_cr1-5f{encodedStem}-20{marker}-5f566c5f3d9705a6dd", internalIdCopy);
    }

    [Theory]
    [InlineData("shared_weatherplus-5f566c5f3d9705a6dd", "Weather Plus")]
    [InlineData("shared_cr1-5fweather-20plus", "Weather Plus")]
    [InlineData("shared_cr1-5fweather-20plus-5f566c5f3d9705", "Weather Plus")]
    [InlineData("custom_cr1-5fweather-20plus-5f566c5f3d9705a6dd", "Weather Plus")]
    [InlineData(CopyTestWorkspace.ConnectorInternalId, "Weather Plus Beta")]
    public async Task Prepare_CopyWithACustomConnectorTheEnvironmentDidNotName_IsBlockedBeforeAnythingIsRewritten(string internalId, string displayName)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector(internalId, displayName);
        var originalTool = workspace.Read(CopyTestWorkspace.ConnectorToolFile);

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.UnsupportedResource && finding.Path == CopyTestWorkspace.ConnectorFolder && finding.Message.Contains(internalId, StringComparison.Ordinal));
        Assert.True(workspace.Exists(CopyTestWorkspace.ConnectorFolder));
        Assert.Equal(originalTool, workspace.Read(CopyTestWorkspace.ConnectorToolFile));
    }
    [Fact]
    public async Task Prepare_CopyWithAConnectorCapturedInAnotherEnvironment_PredictsTheTargetSuffix()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector(CapturedElsewhereInternalId);
        WriteSecondConnector(workspace);
        var target = CopyTestWorkspace.FreeEnvironment();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { CapturedInTargetEnvironment = false, TargetEnvironment = target.Object }, CancellationToken.None);

        var internalIdCopy = Destination(result, CopyIdentityKinds.ConnectorInternalId, CapturedElsewhereInternalId);
        var connectorCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Connector, CopyTestWorkspace.ConnectorId.ToString("D")));
        var referenceCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.ConnectorReference);
        Assert.True(result.Audit.Passed);
        Assert.Matches("^shared_cr1-5fweather-20plus-20[0-9a-f]{6}-5fd6c23f2bd6bf7828$", internalIdCopy);
        Assert.Matches("^shared_cr1-5fmaps-20[0-9a-f]{6}-5fd6c23f2bd6bf7828$", Destination(result, CopyIdentityKinds.ConnectorInternalId, SecondConnectorInternalId));
        using var metadata = JsonDocument.Parse(workspace.Read("connectors/" + workspace.Folder("connectors", connectorCopy) + "/metadata.yml"));
        Assert.Equal(internalIdCopy, metadata.RootElement.GetProperty("connectorinternalid").GetString());
        Assert.Contains($"connectorId: /providers/Microsoft.PowerApps/apis/{internalIdCopy}\n", workspace.Read(CopyTestWorkspace.ConnectorToolFile), StringComparison.Ordinal);
        Assert.Contains($"connectorId: /providers/Microsoft.PowerApps/apis/{internalIdCopy}\n", workspace.Read($"infrastructure/connections/{referenceCopy}.sync.yaml"), StringComparison.Ordinal);
        target.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_CopyWhoseCaptureEnvironmentIsUnknownWithoutATargetSuffix_IsBlockedBeforeAnythingIsRewritten(bool withTargetEnvironment)
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector(CapturedElsewhereInternalId);
        WriteSecondConnector(workspace);
        var originalTool = workspace.Read(CopyTestWorkspace.ConnectorToolFile);
        var target = CopyTestWorkspace.FreeEnvironment(environmentSuffix: null);

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { CapturedInTargetEnvironment = false, TargetEnvironment = withTargetEnvironment ? target.Object : null }, CancellationToken.None));

        var finding = Assert.Single(failure.Findings);
        Assert.Equal(CopyAuditCodes.UnsupportedResource, finding.Code);
        Assert.Contains("Copy from a workspace connected to that environment.", finding.Message, StringComparison.Ordinal);
        Assert.True(workspace.Exists(CopyTestWorkspace.ConnectorFolder));
        Assert.Equal(originalTool, workspace.Read(CopyTestWorkspace.ConnectorToolFile));
        target.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), withTargetEnvironment ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Prepare_CopyCapturedInTheTargetEnvironment_KeepsTheCapturedSuffixAndLetsOnlyTheAuditReadIt()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.WriteConnector(CapturedElsewhereInternalId);
        var target = CopyTestWorkspace.FreeEnvironment(environmentSuffix: "c57fa9bfea132312");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { TargetEnvironment = target.Object }, CancellationToken.None);

        Assert.True(result.Audit.Passed);
        Assert.EndsWith("-5fc57fa9bfea132312", Destination(result, CopyIdentityKinds.ConnectorInternalId, CapturedElsewhereInternalId), StringComparison.Ordinal);
        target.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prepare_WithoutAConnectorToCopy_NeverReadsTheConnectorSuffix()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var target = CopyTestWorkspace.FreeEnvironment();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { CapturedInTargetEnvironment = false, TargetEnvironment = target.Object }, CancellationToken.None);

        Assert.True(result.Audit.Passed);
        target.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prepare_WithATargetEnvironment_ChecksTheNewIdentitiesAreFreeThere()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var target = CopyTestWorkspace.FreeEnvironment();
        var request = workspace.Request() with { TargetEnvironment = target.Object };

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);

        var workflowCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.ClassicWorkflowId.ToString("D")));
        var modelCopy = Guid.Parse(Destination(result, CopyIdentityKinds.AIModel, CopyTestWorkspace.PromptModelId.ToString("D")));
        Assert.True(result.Audit.Passed);
        target.Verify(environment => environment.GetAgentIdBySchemaNameAsync(request.SchemaName, It.IsAny<CancellationToken>()), Times.Once);
        target.Verify(environment => environment.FindComponentSchemaNamesAsync(request.SchemaName + ".", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        target.Verify(environment => environment.FindExistingWorkflowIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { workflowCopy })), It.IsAny<CancellationToken>()), Times.Once);
        target.Verify(environment => environment.FindExistingAIModelIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { modelCopy })), It.IsAny<CancellationToken>()), Times.Once);
        target.Verify(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.Is<IEnumerable<string>>(names => names.Count() == 2), It.IsAny<CancellationToken>()), Times.Once);
        target.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Prepare_IntoASchemaNameAnotherAgentHoldsInTheTargetEnvironment_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var request = workspace.Request();
        var holder = Guid.NewGuid();
        var target = CopyTestWorkspace.FreeEnvironment();
        target.Setup(environment => environment.GetAgentIdBySchemaNameAsync(request.SchemaName, It.IsAny<CancellationToken>())).ReturnsAsync(holder);

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(request with { TargetEnvironment = target.Object }, CancellationToken.None));

        var finding = Assert.Single(failure.Findings);
        Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code);
        Assert.Equal($"An agent with the schema name {request.SchemaName} already exists in the environment ({holder}). Copy the agent again with another schema name.", finding.Message);
    }

    [Fact]
    public async Task Prepare_WhenTheWorkspaceAuditFails_NeverReadsTheTargetEnvironment()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write("knowledge/cr9_other.topic.Foreign_abc123.mcs.yml", "mcs.metadata:\n  componentName: Foreign\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/foreign\n");
        var target = new Mock<ICopyDataverseClient>(MockBehavior.Strict);

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { TargetEnvironment = target.Object }, CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.ForeignComponentSchema);
        target.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(" ", "is required")]
    [InlineData("Help <desk>", "contains < or >")]
    [InlineData("A display name far longer than Copilot Studio accepts", "is 53 characters long")]
    public async Task Prepare_ADisplayNameCopilotStudioRefuses_IsBlockedBeforeAnythingIsRewritten(string displayName, string problem)
    {
        using var workspace = CopyTestWorkspace.Cli();
        var originalSettings = workspace.Read("settings.mcs.yml");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { DisplayName = displayName }, CancellationToken.None));

        var finding = Assert.Single(failure.Findings);
        Assert.Equal(CopyAuditCodes.AgentNameUnsupported, finding.Code);
        Assert.StartsWith("The new agent's display name " + problem, finding.Message, StringComparison.Ordinal);
        Assert.Equal(originalSettings, workspace.Read("settings.mcs.yml"));
    }

    [Fact]
    public async Task Prepare_CliCopyWithASettingsComponent_TheAuditSeesItUnderTheNewSchema()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("settings/Default_FeedbackCollectionBotSettings_jsNFOl.mcs.yml", "kind: DefaultFeedbackCollection\n");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.Contains(CopyTestWorkspace.DestinationSchemaFor(CopyTestWorkspace.CliSchema) + ".BotSettingsComponent.Default_FeedbackCollectionBotSettings_jsNFOl", result.Audit.ComponentSchemaNames);
    }

    [Theory]
    [InlineData("activity: cr2_classic\n", true)]
    [InlineData("customKey: cr2_classic.topic.Greeting\n", true)]
    [InlineData("description: cr2_classic. Answers questions about expenses.\n", false)]
    public async Task Prepare_ClassicCopy_RefusesASourceRootedValueItDoesNotRewriteOnlyWhenItIsShapedLikeAnIdentity(string notes, bool refused)
    {
        using var workspace = CopyTestWorkspace.Classic();
        var topic = "mcs.metadata:\n  componentName: Notes\nkind: AdaptiveDialog\n" + notes;
        workspace.Write("topics/Notes.mcs.yml", topic);

        Assert.Equal(refused ? new[] { $"{CopyAuditCodes.LeftoverSourceIdentity} topics/Notes.mcs.yml" } : Array.Empty<string>(), (await FindingsOfAsync(workspace.Request())).Select(finding => $"{finding.Code} {finding.Path}"));
        Assert.Equal(topic, workspace.Read("topics/Notes.mcs.yml"));
    }

    [Fact]
    public async Task Prepare_AgentCopy_LeavesSkillAndKnowledgePayloadsThatAreNotAgentMetadataByteForByte()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var payload = $"schemaName: {CopyTestWorkspace.CliSchema}.topic.Expenses\nflowId: {OtherWorkflowId:D}\nconnectionReference: {CopyTestWorkspace.CliTeamsReference}\n";
        var payloads = new[] { "behaviors/Expenses/config.yml", "behaviors/Expenses/rates.yaml", "capabilities/knowledge/files/Rates.yml" };

        foreach (var relative in payloads)
        {
            workspace.Write(relative, payload);
        }

        workspace.Write("capabilities/knowledge/files/Rates.mcs.yml", "mcs.metadata:\n  componentName: Rates.yml\nkind: FileAttachmentComponentMetadata\n");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.True(result.Audit.Passed);
        Assert.All(payloads, relative => Assert.Equal(payload, workspace.Read(relative)));
        Assert.Empty(result.RewrittenFiles.Intersect(payloads));
    }

    [Theory]
    [MemberData(nameof(WorkflowStatesAnAgentCopyCannotRewrite))]
    public async Task Prepare_AgentCopy_RefusesAWorkflowItCannotMakeADraft(bool classic, string value, string replacement, bool refused)
    {
        using var workspace = classic ? CopyTestWorkspace.Classic() : CopyTestWorkspace.Cli();
        var metadataPath = (classic ? CopyTestWorkspace.ClassicWorkflowFolder : CopyTestWorkspace.CliWorkflowFolder) + "/metadata.yml";
        workspace.Write(metadataPath, workspace.Read(metadataPath).Replace(value, replacement, StringComparison.Ordinal), preamble: true);

        var findings = await FindingsOfAsync(workspace.Request());

        var copiedMetadataPath = "workflows/" + Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(workspace.Path("workflows")))) + "/metadata.yml";
        Assert.Equal(refused ? new[] { new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, "The copy is not a draft (stateCode 0, statusCode 1): the source's metadata.yml writes the state in a form the copy cannot rewrite. Write it on one line and copy again.", copiedMetadataPath) } : Array.Empty<CopyAuditFinding>(), findings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_AgentCopyOfAFlowWhoseMetadataWritesItsIdAsABlock_IsRefusedAsStillNamingTheSourceFlow(bool classic)
    {
        using var workspace = classic ? CopyTestWorkspace.Classic() : CopyTestWorkspace.Cli();
        var sourceWorkflowId = classic ? CopyTestWorkspace.ClassicWorkflowId : CopyTestWorkspace.CliWorkflowId;
        var metadataPath = (classic ? CopyTestWorkspace.ClassicWorkflowFolder : CopyTestWorkspace.CliWorkflowFolder) + "/metadata.yml";
        workspace.Write(metadataPath, workspace.Read(metadataPath).Replace($"\nworkflowId: {sourceWorkflowId:D}\n", $"\nworkflowId: |-\n  {sourceWorkflowId:D}\n", StringComparison.Ordinal), preamble: true);

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        var copiedWorkflow = Assert.Single(WorkflowWorkspace.FindAll(workspace.Root));
        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The copy still names workflow {sourceWorkflowId:D}, not its own {copiedWorkflow.WorkflowId:D}: the source's metadata.yml writes the id in a form the copy cannot rewrite. Write it on one line and copy again.", $"workflows/{copiedWorkflow.FolderName}/metadata.yml"), Assert.Single(failure.Findings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_AgentCopyOfAFlowCallingAChildFlow_CallsTheChildsCopyOnlyWhenTheAgentHoldsTheChild(bool holdsTheChild)
    {
        using var workspace = CopyTestWorkspace.Classic();
        var childWorkflowId = CopyTestWorkspace.ChildWorkflowId.ToString("D");
        workspace.AddChildFlowRun(CopyTestWorkspace.ClassicWorkflowFolder, CopyTestWorkspace.ChildWorkflowId);

        if (holdsTheChild)
        {
            workspace.WriteWorkflow($"workflows/ChildFlow-{childWorkflowId}", CopyTestWorkspace.ChildWorkflowId, "Child Flow", CopyTestWorkspace.ClassicTeamsReference, CopyTestWorkspace.ClassicWeatherReference);
        }

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var parentDefinition = "workflows/" + workspace.Folder("workflows", Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.ClassicWorkflowId.ToString("D")))) + "/workflow.json";
        Assert.True(result.Audit.Passed);
        Assert.Empty(result.Audit.Notices);
        Assert.Contains($"\"workflowReferenceName\": \"{(holdsTheChild ? Destination(result, CopyIdentityKinds.Workflow, childWorkflowId) : childWorkflowId)}\"", workspace.Read(parentDefinition), StringComparison.Ordinal);
        Assert.Equal(holdsTheChild ? Array.Empty<string>() : new[] { parentDefinition }, Mentioning(workspace, childWorkflowId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_AgentCopyWhoseSettingsWriteTheNameAsABlock_IsRefusedAsStillNamedAfterTheSource(bool classic)
    {
        using var workspace = classic ? CopyTestWorkspace.Classic() : CopyTestWorkspace.Cli();
        var sourceName = classic ? "Classic Source" : "Cli Source";
        workspace.Write("settings.mcs.yml", workspace.Read("settings.mcs.yml").Replace($"displayName: {sourceName}\n", $"displayName: |-\n  {sourceName}\n", StringComparison.Ordinal));

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The copy is still named '{sourceName}', not '{CopyTestWorkspace.DestinationDisplayName}': the source's settings.mcs.yml writes the name in a form the copy cannot rewrite. Write it on one line and copy again.", "settings.mcs.yml"), Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None))).Findings));
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_GivesTheFlowANewIdFolderAndNameAsADraft()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var workflowCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.StandaloneWorkflowId.ToString("D")));
        var workflowFolder = WorkflowWorkspace.RelativeFolderFor(CopyTestWorkspace.StandaloneWorkflowCopyName, workflowCopy);
        var metadata = workspace.Read(workflowFolder + "/metadata.yml");
        Assert.True(result.Audit.Passed);
        Assert.NotEqual(CopyTestWorkspace.StandaloneWorkflowId, workflowCopy);
        Assert.False(workspace.Exists(CopyTestWorkspace.StandaloneWorkflowFolder));
        Assert.Equal(workspace.Path(workflowFolder), Assert.Single(Directory.EnumerateDirectories(workspace.Path("workflows"))));
        Assert.Contains(result.RenamedPaths, renamed => renamed.Source == CopyTestWorkspace.StandaloneWorkflowFolder && renamed.Destination == workflowFolder);
        Assert.Contains($"\nworkflowId: {workflowCopy:D}\n", metadata, StringComparison.Ordinal);
        Assert.Contains("\nname: Approve expenses copy\n", metadata, StringComparison.Ordinal);
        Assert.StartsWith($"jsonFileName: {workflowFolder}/workflow.json\n", metadata, StringComparison.Ordinal);
        Assert.Contains("\nstateCode: 0\n", metadata, StringComparison.Ordinal);
        Assert.Contains("\nstatusCode: 1\n", metadata, StringComparison.Ordinal);
        Assert.Equal(new[] { CopyTestWorkspace.StandaloneWorkflowCopyName }, result.DraftWorkflows);
        Assert.Equal(new[] { CopyIdentityKinds.Workflow, CopyIdentityKinds.ConnectionReference, CopyIdentityKinds.ConnectionReference, CopyIdentityKinds.ConnectionReference }, result.Identities.Select(mapping => mapping.Kind));
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_RenamesEachReferenceInMetadataDefinitionAndDeclarations()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var agentRootedCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.AgentRootedReference);
        var hexSuffixedCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.HexSuffixedReference);
        var unsuffixedCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.UnsuffixedReference);
        var workflowFolder = workspace.CopiedWorkflowFolder(result);
        var metadata = workspace.Read(workflowFolder + "/metadata.yml");
        var definition = workspace.Read(workflowFolder + "/workflow.json");
        Assert.True(result.Audit.Passed);
        Assert.Matches(@"^cre98_AgentA1\.shared_commondataserviceforapps\.[0-9a-f]{32}$", agentRootedCopy);
        Assert.NotEqual(CopyTestWorkspace.AgentRootedReference, agentRootedCopy);
        Assert.Matches("^new_sharedcommondataserviceforapps_[0-9a-f]{8}$", hexSuffixedCopy);
        Assert.NotEqual(CopyTestWorkspace.HexSuffixedReference, hexSuffixedCopy);
        Assert.Matches("^new_sharedoffice365_[0-9a-f]{8}$", unsuffixedCopy);

        foreach (var (source, copy) in new[] { (CopyTestWorkspace.AgentRootedReference, agentRootedCopy), (CopyTestWorkspace.HexSuffixedReference, hexSuffixedCopy), (CopyTestWorkspace.UnsuffixedReference, unsuffixedCopy) })
        {
            Assert.Contains($"\n- {copy}\n", metadata, StringComparison.Ordinal);
            Assert.DoesNotContain($"\n- {source}\n", metadata, StringComparison.Ordinal);
            Assert.Contains($"\"connectionReferenceLogicalName\": \"{copy}\"", definition, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{source}\"", definition, StringComparison.Ordinal);
            Assert.Contains($"connectionReferenceLogicalName: {copy}", workspace.Read($"infrastructure/connections/{copy}.sync.yaml"), StringComparison.Ordinal);
            Assert.False(workspace.Exists($"infrastructure/connections/{source}.sync.yaml"));
        }

        Assert.Contains(result.RenamedPaths, renamed => renamed.Source == $"infrastructure/connections/{CopyTestWorkspace.AgentRootedReference}.sync.yaml" && renamed.Destination == $"infrastructure/connections/{agentRootedCopy}.sync.yaml");
        Assert.Contains(result.RenamedPaths, renamed => renamed.Source == $"infrastructure/connections/{CopyTestWorkspace.HexSuffixedReference}.sync.yaml" && renamed.Destination == $"infrastructure/connections/{hexSuffixedCopy}.sync.yaml");
        Assert.DoesNotContain(result.RenamedPaths, renamed => renamed.Destination == $"infrastructure/connections/{unsuffixedCopy}.sync.yaml");
        Assert.Contains($"infrastructure/connections/{unsuffixedCopy}.sync.yaml", result.RewrittenFiles);
        Assert.Equal(3, Directory.EnumerateFiles(workspace.Path("infrastructure/connections")).Count());
        Assert.EndsWith("connectorId: /providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps", workspace.Read($"infrastructure/connections/{agentRootedCopy}.sync.yaml").TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("connectorId: /providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps", workspace.Read($"infrastructure/connections/{hexSuffixedCopy}.sync.yaml").TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("connectorId: /providers/Microsoft.PowerApps/apis/shared_office365", workspace.Read($"infrastructure/connections/{unsuffixedCopy}.sync.yaml").TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_DeclaresAReferenceWithoutAnApiUnderTheConnectorItsKeyNames()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var definitionPath = CopyTestWorkspace.StandaloneWorkflowFolder + "/workflow.json";
        workspace.Write(definitionPath, workspace.Read(definitionPath).Replace("\"shared_office365\": {\n        \"api\": {\n          \"name\": \"shared_office365\"\n        },\n", "\"shared_office365\": {\n", StringComparison.Ordinal), preamble: true);

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var unsuffixedCopy = Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.UnsuffixedReference);
        var copiedDefinition = workspace.Read(workspace.CopiedWorkflowFolder(result) + "/workflow.json");
        Assert.True(result.Audit.Passed);
        Assert.DoesNotContain("\"name\": \"shared_office365\"", copiedDefinition, StringComparison.Ordinal);
        Assert.Contains($"\"connectionReferenceLogicalName\": \"{unsuffixedCopy}\"", copiedDefinition, StringComparison.Ordinal);
        Assert.EndsWith("connectorId: /providers/Microsoft.PowerApps/apis/shared_office365", workspace.Read($"infrastructure/connections/{unsuffixedCopy}.sync.yaml").TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_KeepsCallsToResourcesItDoesNotHoldAndReportsNothing()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var definition = workspace.Read(workspace.CopiedWorkflowFolder(result) + "/workflow.json");
        Assert.True(result.Audit.Passed);
        Assert.Empty(result.Audit.Notices);
        Assert.Contains($"\"recordId\": \"{CopyTestWorkspace.CalledPromptId:D}\"", definition, StringComparison.Ordinal);
        Assert.Contains($"\"workflowReferenceName\": \"{CopyTestWorkspace.ChildWorkflowId:D}\"", definition, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Identities, mapping => mapping.Source == CopyTestWorkspace.CalledPromptId.ToString("D") || mapping.Source == CopyTestWorkspace.ChildWorkflowId.ToString("D"));
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_IgnoresTheAgentOnlyRequestFields()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        workspace.Write("botdefinition.json", "{\"$kind\":\"BotDefinition\",\"dataverseTableSearches\":[{\"id\":\"x\"}]}");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { SourceSchemaName = "cr9_other", SchemaName = "cr9_copy", SourceDefinitionPath = workspace.Path("botdefinition.json") }, CancellationToken.None);

        Assert.True(result.Audit.Passed);
        Assert.DoesNotContain(result.Identities, mapping => mapping.Kind == CopyIdentityKinds.Schema);
        Assert.StartsWith("cre98_AgentA1.shared_commondataserviceforapps.", Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.AgentRootedReference), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_LeavesEveryOtherByteAlone()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        workspace.Write("notes.yml", $"connectionReference: {CopyTestWorkspace.HexSuffixedReference}\nworkflowId: {CopyTestWorkspace.StandaloneWorkflowId:D}\n");
        var untouched = new[] { "agent.sync.yaml", "workflow.sync.yaml", "notes.yml" }.ToDictionary(relative => relative, relative => File.ReadAllBytes(workspace.Path(relative)));
        var originalMetadata = workspace.Read(CopyTestWorkspace.StandaloneWorkflowFolder + "/metadata.yml");
        var originalDefinition = workspace.Read(CopyTestWorkspace.StandaloneWorkflowFolder + "/workflow.json");

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        var workflowCopy = Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.StandaloneWorkflowId.ToString("D"));
        var workflowFolder = workspace.CopiedWorkflowFolder(result);
        var references = result.Identities.Where(mapping => mapping.Kind == CopyIdentityKinds.ConnectionReference).ToArray();
        Assert.True(result.Audit.Passed);
        Assert.All(untouched, file => Assert.Equal(file.Value, File.ReadAllBytes(workspace.Path(file.Key))));
        Assert.Equal(
            references.Aggregate(
                originalMetadata
                    .Replace($"jsonFileName: {CopyTestWorkspace.StandaloneWorkflowFolder}/", $"jsonFileName: {workflowFolder}/", StringComparison.Ordinal)
                    .Replace($"\nworkflowId: {CopyTestWorkspace.StandaloneWorkflowId:D}\n", $"\nworkflowId: {workflowCopy}\n", StringComparison.Ordinal)
                    .Replace("\nname: Approve expenses\n", "\nname: Approve expenses copy\n", StringComparison.Ordinal)
                    .Replace("\nstateCode: 1\n", "\nstateCode: 0\n", StringComparison.Ordinal)
                    .Replace("\nstatusCode: 2\n", "\nstatusCode: 1\n", StringComparison.Ordinal),
                (text, mapping) => text.Replace($"\n- {mapping.Source}\n", $"\n- {mapping.Destination}\n", StringComparison.Ordinal)),
            workspace.Read(workflowFolder + "/metadata.yml"));
        Assert.Equal(references.Aggregate(originalDefinition, (text, mapping) => text.Replace($"\"connectionReferenceLogicalName\": \"{mapping.Source}\"", $"\"connectionReferenceLogicalName\": \"{mapping.Destination}\"", StringComparison.Ordinal)), workspace.Read(workflowFolder + "/workflow.json"));
        Assert.True(workspace.HasPreamble(workflowFolder + "/metadata.yml"));
        Assert.True(workspace.HasPreamble(workflowFolder + "/workflow.json"));
        Assert.True(workspace.HasPreamble($"infrastructure/connections/{Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.HexSuffixedReference)}.sync.yaml"));
        Assert.False(workspace.HasPreamble($"infrastructure/connections/{Destination(result, CopyIdentityKinds.ConnectionReference, CopyTestWorkspace.AgentRootedReference)}.sync.yaml"));
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_LeavesPromptAndConnectorFoldersAlone()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var promptFolder = $"prompts/Summarize-{CopyTestWorkspace.CalledPromptId:D}";
        workspace.Write(promptFolder + "/metadata.yml", $"aIModelId: {CopyTestWorkspace.CalledPromptId:D}\nname: Summarize\n", preamble: true);
        workspace.Write(promptFolder + "/prompt.json", "{\n  \"name\": \"Summarize\",\n  \"instruction\": \"Summarize the expense report.\"\n}\n");
        workspace.Write("prompts/Draft-9a8b7c6d-5e4f-4a3b-9c2d-1e0f9a8b7c6d/metadata.yml", "aIModelId: 9a8b7c6d-5e4f-4a3b-9c2d-1e0f9a8b7c6d\nname: Draft\n");
        workspace.WriteConnectorFolder(CopyTestWorkspace.ConnectorFolder, CopyTestWorkspace.ConnectorId, CopyTestWorkspace.ConnectorName, "Weather Plus", CopyTestWorkspace.ConnectorInternalId, "weather.example.com", null);
        var resources = workspace.Snapshot().Where(IsPromptOrConnectorFile).ToArray();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);

        Assert.True(result.Audit.Passed);
        Assert.Equal(6, resources.Length);
        Assert.Equal(resources, workspace.Snapshot().Where(IsPromptOrConnectorFile));
        Assert.Equal(new[] { CopyIdentityKinds.Workflow, CopyIdentityKinds.ConnectionReference }, result.Identities.Select(mapping => mapping.Kind).Distinct());
        Assert.Contains($"\"recordId\": \"{CopyTestWorkspace.CalledPromptId:D}\"", workspace.Read(workspace.CopiedWorkflowFolder(result) + "/workflow.json"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agent project")]
    [InlineData("half-written flow")]
    [InlineData("no flow")]
    [InlineData("two flows")]
    [InlineData("unparseable metadata")]
    [InlineData("metadata naming another id")]
    [InlineData("another source")]
    public async Task Prepare_WorkflowCopy_RefusesALayoutItCannotCopy(string layout)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var request = workspace.Request();
        var metadataPath = CopyTestWorkspace.StandaloneWorkflowFolder + "/metadata.yml";
        var secondFolder = WorkflowWorkspace.RelativeFolderFor("Second", OtherWorkflowId);

        switch (layout)
        {
            case "agent project":
                workspace.Write("settings.mcs.yml", "displayName: Approvals\nschemaName: cre98_approvals\n");
                break;
            case "half-written flow":
                workspace.Write(secondFolder + "/workflow.json", "{}\n");
                break;
            case "no flow":
                workspace.Delete(CopyTestWorkspace.StandaloneWorkflowFolder);
                break;
            case "two flows":
                workspace.Write(secondFolder + "/workflow.json", workspace.Read(CopyTestWorkspace.StandaloneWorkflowFolder + "/workflow.json"));
                workspace.Write(secondFolder + "/metadata.yml", workspace.Read(metadataPath).Replace(CopyTestWorkspace.StandaloneWorkflowId.ToString("D"), OtherWorkflowId.ToString("D"), StringComparison.Ordinal));
                break;
            case "unparseable metadata":
                workspace.Write(metadataPath, "workflowId: [unclosed\n");
                break;
            case "metadata naming another id":
                workspace.Write(metadataPath, workspace.Read(metadataPath).Replace($"workflowId: {CopyTestWorkspace.StandaloneWorkflowId:D}", $"workflowId: {OtherWorkflowId:D}", StringComparison.Ordinal), preamble: true);
                break;
            case "another source":
                request = request with { SourceWorkflowId = OtherWorkflowId };
                break;
        }

        var expected = layout switch
        {
            "agent project" => "The workspace holds an agent project, so it is not a standalone workflow.",
            "half-written flow" => "A workflow folder is missing its definition or its metadata.",
            "another source" => $"The workspace holds workflow {CopyTestWorkspace.StandaloneWorkflowId}, not the selected source {OtherWorkflowId}.",
            _ => await workspace.PushReaderFailureAsync(),
        };
        var before = workspace.Snapshot();

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.Unreadable, expected), Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None))).Findings));
        Assert.Equal(before, workspace.Snapshot());
    }

    [Theory]
    [InlineData("\ncategory: 5\n", "\ncategory: 0\n", "The workflow is not a cloud flow (category 0, type 1), so it cannot be copied.")]
    [InlineData("\ncategory: 5\n", "\ncategory: 6\n", "The workflow is not a cloud flow (category 6, type 1), so it cannot be copied.")]
    [InlineData("\ntype: 1\n", "\ntype: 2\n", "The workflow is not a cloud flow (category 5, type 2), so it cannot be copied.")]
    [InlineData("\ntype: 1\n", "\ntype: 3\n", "The workflow is not a cloud flow (category 5, type 3), so it cannot be copied.")]
    [InlineData("\ncategory: 5\n", "\n", "The workflow's metadata does not name its category and type, so a copy cannot be created as a cloud flow.")]
    [InlineData("\ntype: 1\n", "\n", "The workflow's metadata does not name its category and type, so a copy cannot be created as a cloud flow.")]
    public async Task Prepare_WorkflowCopy_RefusesAFlowThatIsNotACloudFlow(string code, string replacement, string message)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var metadataPath = CopyTestWorkspace.StandaloneWorkflowFolder + "/metadata.yml";
        workspace.Write(metadataPath, workspace.Read(metadataPath).Replace(code, replacement, StringComparison.Ordinal), preamble: true);
        var before = workspace.Snapshot();

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, message), Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None))).Findings));
        Assert.Equal(before, workspace.Snapshot());
    }

    [Theory]
    [InlineData("{\"properties\":{\"connectionReferences\":[]}}")]
    [InlineData("{\"properties\":{\"connectionReferences\":{\"shared_office365\":\"not an object\"}}}")]
    [InlineData("[]")]
    public async Task Prepare_WorkflowCopy_WithADefinitionWhoseReferencesPushCannotRead_IsRefused(string definition)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var definitionPath = CopyTestWorkspace.StandaloneWorkflowFolder + "/workflow.json";
        workspace.Write(definitionPath, definition);
        var before = workspace.Snapshot();

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.Unreadable, "The workflow definition declares its connection references in a shape a push cannot read, so they cannot be rewritten.", definitionPath), Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None))).Findings));
        Assert.Equal(before, workspace.Snapshot());
    }

    [Theory]
    [InlineData("{\n  \"properties\": {", "{\n  // Routes each expense report to its approver.\n  \"properties\": {")]
    [InlineData("\"schemaVersion\": \"1.0.0.0\"\n}", "\"schemaVersion\": \"1.0.0.0\",\n}")]
    public async Task Prepare_WorkflowCopy_WithADefinitionADeployCannotParse_IsRefused(string strictJson, string relaxedJson)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var definitionPath = CopyTestWorkspace.StandaloneWorkflowFolder + "/workflow.json";
        workspace.Write(definitionPath, workspace.Read(definitionPath).Replace(strictJson, relaxedJson, StringComparison.Ordinal), preamble: true);
        var before = workspace.Snapshot();

        var finding = Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None))).Findings);
        Assert.Equal(CopyAuditCodes.Unreadable, finding.Code);
        Assert.Equal(definitionPath, finding.Path);
        Assert.Matches(@"^The workflow definition is not valid JSON \(.+\), so its connection references cannot be rewritten\.$", finding.Message);
        Assert.Equal(before, workspace.Snapshot());
    }

    [Theory]
    [MemberData(nameof(NewWorkflowNames))]
    public async Task Prepare_WorkflowCopy_RefusesANameTheServiceWouldRefuse(string name, string? code, string? message)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();

        Assert.Equal(code is null ? Array.Empty<CopyAuditFinding>() : new[] { new CopyAuditFinding(code, message ?? string.Empty) }, await FindingsOfAsync(workspace.Request() with { DisplayName = name }));
    }

    [Theory]
    [MemberData(nameof(MetadataValuesTheCopyCannotRewrite))]
    public async Task Prepare_WorkflowCopy_RefusesACopyItCannotNameOrMakeADraft(string value, string replacement, string? message)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var metadataPath = CopyTestWorkspace.StandaloneWorkflowFolder + "/metadata.yml";
        workspace.Write(metadataPath, workspace.Read(metadataPath).Replace(value, replacement, StringComparison.Ordinal), preamble: true);

        Assert.Equal(message is null ? Array.Empty<CopyAuditFinding>() : new[] { new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, message) }, await FindingsOfAsync(workspace.Request()));
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_WithAReferenceTooLongToRefresh_IsBlockedWithoutSchemaAdvice()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var longReference = "new_" + new string('x', 188);
        workspace.Write($"infrastructure/connections/{longReference}.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {longReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n");
        var before = workspace.Snapshot();

        var finding = Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None))).Findings);
        Assert.Equal(CopyAuditCodes.SchemaTooLong, finding.Code);
        Assert.Matches($"^The connection reference {longReference} would be renamed to {longReference}_[0-9a-f]{{8}}, which is longer than 200 characters\\.$", finding.Message);
        Assert.Equal(before, workspace.Snapshot());
    }

    [Fact]
    public async Task Prepare_AgentKindOnAWorkflowWorkspace_IsRefusedAsToday()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.Unreadable, "The workspace has no settings.mcs.yml, so it is not an agent workspace.", "settings.mcs.yml"), Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(new CopyPreparationRequest { WorkspacePath = workspace.Root, DisplayName = CopyTestWorkspace.StandaloneWorkflowCopyName, SchemaName = "cre98_approveexpensescopy" }, CancellationToken.None))).Findings));
    }

    [Fact]
    public async Task Prepare_WithAnUnknownWorkspaceKind_ThrowsBeforeReadingAnything()
    {
        var target = new Mock<ICopyDataverseClient>(MockBehavior.Strict);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => CopyTestWorkspace.CreateService().PrepareAsync(new CopyPreparationRequest { WorkspacePath = Path.Combine(Path.GetTempPath(), "copytests-missing-" + Guid.NewGuid().ToString("N")), WorkspaceKind = "flow", DisplayName = CopyTestWorkspace.StandaloneWorkflowCopyName, TargetEnvironment = target.Object }, CancellationToken.None));

        Assert.Equal("request", failure.ParamName);
        Assert.StartsWith("A copy prepares an 'agent' or a 'workflow' workspace, not 'flow'.", failure.Message, StringComparison.Ordinal);
        target.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(SchemaNamesAnAgentCopyCannotTake))]
    public async Task Prepare_AgentCopyUnderAnInvalidSchemaName_ThrowsBeforeTouchingAnything(string schemaName)
    {
        using var workspace = CopyTestWorkspace.Classic();
        var besideTheWorkspace = Path.GetDirectoryName(workspace.Root) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.");
        var neighbors = Directory.EnumerateFileSystemEntries(besideTheWorkspace, "escaped*").ToArray();
        var before = workspace.Snapshot();

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(destinationSchema: schemaName), CancellationToken.None));

        Assert.Equal("request", failure.ParamName);
        Assert.StartsWith($"An agent copy needs a schema name of at most 100 ASCII letters, digits, and the characters _ - . {{ }} !, not '{schemaName}'.", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, workspace.Snapshot());
        Assert.Equal(neighbors, Directory.EnumerateFileSystemEntries(besideTheWorkspace, "escaped*"));
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_WithATargetEnvironment_ReadsFlowIdsReferenceNamesAndTheName()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var target = CopyTestWorkspace.FreeEnvironment();

        var result = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { TargetEnvironment = target.Object }, CancellationToken.None);

        var workflowCopy = Guid.Parse(Destination(result, CopyIdentityKinds.Workflow, CopyTestWorkspace.StandaloneWorkflowId.ToString("D")));
        var referenceCopies = result.Identities.Where(mapping => mapping.Kind == CopyIdentityKinds.ConnectionReference).Select(mapping => mapping.Destination).ToArray();
        Assert.True(result.Audit.Passed);
        Assert.Equal(3, referenceCopies.Length);
        target.Verify(environment => environment.FindCloudFlowsNamedAsync(CopyTestWorkspace.StandaloneWorkflowCopyName, It.IsAny<CancellationToken>()), Times.Once);
        target.Verify(environment => environment.FindExistingWorkflowIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { workflowCopy })), It.IsAny<CancellationToken>()), Times.Once);
        target.Verify(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.Is<IEnumerable<string>>(names => names.SequenceEqual(referenceCopies)), It.IsAny<CancellationToken>()), Times.Once);
        target.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(CopyIdentityKinds.Workflow, "The workflow ")]
    [InlineData(CopyIdentityKinds.ConnectionReference, "The connection reference ")]
    public async Task Prepare_WorkflowCopy_WhoseNewIdentityIsTaken_AsksToCopyTheWorkflowAgain(string takenKind, string subject)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var target = CopyTestWorkspace.FreeEnvironment();

        if (takenKind == CopyIdentityKinds.Workflow)
        {
            target.Setup(environment => environment.FindExistingWorkflowIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<Guid> identifiers, CancellationToken _) => identifiers.ToArray());
        }
        else
        {
            target.Setup(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<string> names, CancellationToken _) => names.Select(name => new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = name }).ToArray());
        }

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { TargetEnvironment = target.Object }, CancellationToken.None));

        Assert.Equal(takenKind == CopyIdentityKinds.Workflow ? 1 : 3, failure.Findings.Count);
        Assert.All(failure.Findings, finding =>
        {
            Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code);
            Assert.StartsWith(subject, finding.Message, StringComparison.Ordinal);
            Assert.EndsWith(" this copy would create already exists in the environment. Copy the workflow again.", finding.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_UnderANameAnotherFlowHas_IsRefused()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var target = CopyTestWorkspace.FreeEnvironment(cloudFlowsWithTheName: new[] { new SyncDataverseClient.WorkflowSummary { WorkflowId = OtherWorkflowId, Name = "approve EXPENSES copy" } });

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"The environment already has a workflow named 'approve EXPENSES copy' ({OtherWorkflowId}). Copy the workflow again under another name."), Assert.Single((await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request() with { TargetEnvironment = target.Object }, CancellationToken.None))).Findings));
        target.Verify(environment => environment.FindCloudFlowsNamedAsync(CopyTestWorkspace.StandaloneWorkflowCopyName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prepare_WorkflowCopy_HonorsCancellation()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = workspace.Snapshot();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), cancellation.Token));

        Assert.Equal(before, workspace.Snapshot());
    }

    private static string Destination(CopyPreparationResult result, string kind, string source) => result.Identities.Single(mapping => mapping.Kind == kind && string.Equals(mapping.Source, source, StringComparison.OrdinalIgnoreCase)).Destination;

    private static bool IsPromptOrConnectorFile(string snapshotEntry) => snapshotEntry.StartsWith("prompts/", StringComparison.Ordinal) || snapshotEntry.StartsWith("connectors/", StringComparison.Ordinal);

    private static IEnumerable<string> Mentioning(CopyTestWorkspace workspace, string text) => Directory.EnumerateFileSystemEntries(workspace.Root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(workspace.Root, path).Replace('\\', '/')).Where(relative => relative.Contains(text, StringComparison.OrdinalIgnoreCase) || (File.Exists(workspace.Path(relative)) && workspace.Read(relative).Contains(text, StringComparison.OrdinalIgnoreCase))).OrderBy(relative => relative, StringComparer.Ordinal);

    private static async Task<IReadOnlyList<CopyAuditFinding>> FindingsOfAsync(CopyPreparationRequest request)
    {
        try
        {
            return (await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None)).Audit.Blockers;
        }
        catch (CopyPreparationException failure)
        {
            return failure.Findings;
        }
    }

    private static void WriteSecondConnector(CopyTestWorkspace workspace) => workspace.Write("connectors/cr1_5Fmaps-2c1d3e5f-9a2b-4c6d-8e0f-1a3b5c7d9e2f/metadata.yml", "{\n  \"connectorid\": \"2c1d3e5f-9a2b-4c6d-8e0f-1a3b5c7d9e2f\",\n  \"name\": \"cr1_5Fmaps\",\n  \"displayname\": \"Maps\",\n  \"connectorinternalid\": \"" + SecondConnectorInternalId + "\",\n  \"connectortype\": 1\n}");

    private static string WriteDuplicateConnector(CopyTestWorkspace workspace, string name, long? version)
    {
        var folder = $"connectors/{name}-{DuplicateConnectorId:D}";
        workspace.WriteConnectorFolder(folder, DuplicateConnectorId, name, "Weather Plus", CopyTestWorkspace.ConnectorInternalId, "stale.example.com", version);

        return folder;
    }

    private static void RenameTool(CopyTestWorkspace workspace, string toolFile, string name, string newName) => workspace.Write(toolFile, workspace.Read(toolFile).Replace($"componentName: {name}\n", $"componentName: {newName}\n", StringComparison.Ordinal));

    private static string CopiedConnectorDisplayName(CopyTestWorkspace workspace, CopyPreparationResult result)
    {
        using var metadata = JsonDocument.Parse(workspace.Read("connectors/" + workspace.Folder("connectors", Guid.Parse(Destination(result, CopyIdentityKinds.Connector, CopyTestWorkspace.ConnectorId.ToString("D")))) + "/metadata.yml"));

        return metadata.RootElement.GetProperty("displayname").GetString() ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.");
    }
}
