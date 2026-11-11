using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CopilotStudio.Sync.Copy;
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

    [Fact]
    public async Task Prepare_WithUnparseableConnectorMetadata_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Cli();
        workspace.Write("connectors/broken/metadata.yml", "{ not json");

        var failure = await Assert.ThrowsAsync<CopyPreparationException>(() => CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None));

        Assert.Contains(failure.Findings, finding => finding.Code == CopyAuditCodes.Unreadable && finding.Path == "connectors/broken/metadata.yml" && finding.Message.StartsWith("The custom connector metadata could not be parsed (", StringComparison.Ordinal));
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

    private static string Destination(CopyPreparationResult result, string kind, string source) => result.Identities.Single(mapping => mapping.Kind == kind && string.Equals(mapping.Source, source, StringComparison.OrdinalIgnoreCase)).Destination;

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
