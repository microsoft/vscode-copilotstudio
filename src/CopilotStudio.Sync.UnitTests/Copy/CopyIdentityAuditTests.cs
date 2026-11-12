using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Copy;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class CopyIdentityAuditTests
{
    private const string DestinationSchema = "cr1_copy_a1b2c3";
    private const string OwnedReference = "cr1_copy_a1b2c3.cr.shared_teams";
    private const string OwnedInternalIdPrefix = "shared_cr1-5fweather-20plus-20a1b2c3-5f";
    private const string OwnedInternalId = OwnedInternalIdPrefix + "d6c23f2bd6bf7828";

    private static readonly Guid OwnedWorkflow = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid OwnedModel = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid OwnedConnector = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
    private static readonly Guid SecondWorkflow = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");
    private static readonly CopyPreparationRequest AgentCopyRequest = new();

    [Fact]
    public async Task AuditWorkspace_AComponentCallingTheSourceWorkflowAgain_IsALeftover()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var workflowCopy = Destination(prepared, CopyIdentityKinds.Workflow);
        workspace.Write("actions/ClassicFlow.mcs.yml", workspace.Read("actions/ClassicFlow.mcs.yml").Replace(workflowCopy, CopyTestWorkspace.ClassicWorkflowId.ToString("D"), StringComparison.Ordinal));

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.LeftoverSourceIdentity, finding.Code);
        Assert.Equal("actions/ClassicFlow.mcs.yml", finding.Path);
    }

    [Fact]
    public async Task AuditWorkspace_ADeclarationStillNamingTheSourceReference_IsALeftover()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write(CopyTestWorkspace.TeamsToolFile, workspace.Read(CopyTestWorkspace.TeamsToolFile).Replace(Destination(prepared, CopyIdentityKinds.Schema) + ".cr.shared_teams", CopyTestWorkspace.CliTeamsReference, StringComparison.Ordinal));

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.LeftoverSourceIdentity && finding.Path == CopyTestWorkspace.TeamsToolFile);
    }

    [Fact]
    public async Task AuditWorkspace_ACopiedWorkflowStillUsingASourceReference_IsALeftover()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var weatherCopy = prepared.Identities.Single(mapping => mapping.Source == CopyTestWorkspace.ClassicWeatherReference).Destination;
        var metadataPath = "workflows/" + workspace.Folder("workflows", Guid.Parse(Destination(prepared, CopyIdentityKinds.Workflow))) + "/metadata.yml";
        workspace.Write(metadataPath, workspace.Read(metadataPath).Replace(weatherCopy, CopyTestWorkspace.ClassicWeatherReference, StringComparison.Ordinal), preamble: true);

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.LeftoverSourceIdentity && finding.Path == metadataPath);
    }

    [Fact]
    public async Task AuditWorkspace_ACallToAnUnknownPrompt_IsUnresolved()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write("actions/Unknown.mcs.yml", "mcs.metadata:\n  componentName: Unknown\nkind: TaskDialog\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: 99999999-2222-3333-4444-555555555555\n\noutputMode: All\n");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.UnresolvedReference && finding.Path == "actions/Unknown.mcs.yml");
    }

    [Fact]
    public async Task AuditWorkspace_AComponentOfAnotherAgent_IsForeign()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write("knowledge/cr9_other.topic.Foreign_abc123.mcs.yml", "mcs.metadata:\n  componentName: Foreign\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/foreign\n");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.ForeignComponentSchema && finding.Message.Contains("cr9_other.topic.Foreign_abc123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuditWorkspace_ASourceComponentLeftBehind_IsALeftover()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write(CopyTestWorkspace.ClassicKnowledgeFile, "mcs.metadata:\n  componentName: Handbook\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/handbook\n");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.LeftoverSourceIdentity && finding.Message.Contains("cr2_classic.topic.Handbook_abc123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(80, "Choose a schema name of at most 13 characters.")]
    [InlineData(95, "Shorten the component's name; no schema name leaves it room.")]
    public async Task AuditWorkspace_AComponentSchemaOverTheLimit_NamesTheLongestSchemaNameThatFits(int topicNameLength, string remedy)
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var topicName = new string('t', topicNameLength);
        workspace.Write($"topics/{topicName}.mcs.yml", workspace.Read("topics/Greeting.mcs.yml"));

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.SchemaTooLong, finding.Code);
        Assert.Equal($"The component schema {Destination(prepared, CopyIdentityKinds.Schema)}.topic.{topicName} is longer than 100 characters. {remedy}", finding.Message);
    }

    [Fact]
    public async Task AuditWorkspace_ADuplicatedSchemaName_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var destinationSchema = Destination(prepared, CopyIdentityKinds.Schema);
        var greeting = destinationSchema + ".topic.Greeting";
        var agent = new BotEntity.Builder { SchemaName = new BotEntitySchemaName(destinationSchema), DisplayName = CopyTestWorkspace.DestinationDisplayName, CdsBotId = Guid.NewGuid() }.Build();
        WorkspaceSynchronizer.WriteCloudCache(WorkflowWorkspace.OpenWorkspace(workspace.Root), new BotDefinition().WithEntity(agent).WithComponents(new[] { Topic(greeting), Topic(greeting) }));
        workspace.Write(".mcs/conn.json", "{}");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.Unreadable, finding.Code);
        Assert.Contains($"{greeting} 2 times", finding.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { greeting }, report.ComponentSchemaNames.Where(schema => schema.StartsWith(greeting, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Cli Source", "Cli Source")]
    [InlineData("team copy", "team copy")]
    [InlineData("\"\"", "")]
    public async Task AuditWorkspace_AnAgentStillNamedOtherThanTheCopy_IsALeftover(string displayName, string effectiveName)
    {
        using var workspace = CopyTestWorkspace.Cli();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write("settings.mcs.yml", workspace.Read("settings.mcs.yml").Replace($"displayName: {CopyTestWorkspace.DestinationDisplayName}\n", $"displayName: {displayName}\n", StringComparison.Ordinal));

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The copy is still named '{effectiveName}', not '{CopyTestWorkspace.DestinationDisplayName}': the source's settings.mcs.yml writes the name in a form the copy cannot rewrite. Write it on one line and copy again.", "settings.mcs.yml"), Assert.Single(report.Blockers));
    }

    [Fact]
    public async Task AuditWorkspace_ACopiedFlowStillRunningTheSourcesPrompt_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var workflowFolder = "workflows/" + workspace.Folder("workflows", Guid.Parse(Destination(prepared, CopyIdentityKinds.Workflow)));
        workspace.AddPromptRun(workflowFolder, CopyTestWorkspace.PromptModelId);

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.LeftoverSourceIdentity, finding.Code);
        Assert.Equal(workflowFolder + "/workflow.json", finding.Path);
        Assert.Contains(CopyTestWorkspace.PromptModelId.ToString("D"), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditWorkspace_ACopiedFlowStillCallingASourceWorkflow_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var workflowFolder = "workflows/" + workspace.Folder("workflows", Guid.Parse(Destination(prepared, CopyIdentityKinds.Workflow)));
        workspace.AddChildFlowRun(workflowFolder, CopyTestWorkspace.ClassicWorkflowId);

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The workflow definition still calls the source's workflow {CopyTestWorkspace.ClassicWorkflowId} instead of its copy.", workflowFolder + "/workflow.json"), Assert.Single((await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None)).Blockers));
    }

    [Theory]
    [InlineData(CopyIdentityKinds.AIModel)]
    [InlineData(CopyIdentityKinds.Connector)]
    [InlineData(CopyIdentityKinds.ConnectorInternalId)]
    public async Task AuditWorkspace_AFolderStillHoldingASourcePromptOrConnector_IsALeftover(string kind)
    {
        using var workspace = kind == CopyIdentityKinds.AIModel ? CopyTestWorkspace.Classic() : CopyTestWorkspace.Cli();

        if (kind != CopyIdentityKinds.AIModel)
        {
            workspace.WriteConnector();
        }

        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        CopyAuditFinding expected;

        if (kind == CopyIdentityKinds.AIModel)
        {
            workspace.Write(CopyTestWorkspace.PromptFolder + "/metadata.yml", $"aIModelId: {CopyTestWorkspace.PromptModelId:D}\nname: prompt one\n");
            expected = new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The AI prompt folder still names the source's AI prompt {CopyTestWorkspace.PromptModelId:D} instead of its copy.", CopyTestWorkspace.PromptFolder);
        }
        else if (kind == CopyIdentityKinds.Connector)
        {
            workspace.WriteConnectorFolder(CopyTestWorkspace.ConnectorFolder, CopyTestWorkspace.ConnectorId, CopyTestWorkspace.ConnectorName, "Weather Plus", OwnedInternalId, "weather.example.com", version: null);
            expected = new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The custom connector folder still names the source's custom connector {CopyTestWorkspace.ConnectorId:D} instead of its copy.", CopyTestWorkspace.ConnectorFolder);
        }
        else
        {
            var folder = $"connectors/stale-{OwnedConnector:D}";
            workspace.WriteConnectorFolder(folder, OwnedConnector, CopyTestWorkspace.ConnectorName, "Weather Plus", CopyTestWorkspace.ConnectorInternalId, "weather.example.com", version: null);
            expected = new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The custom connector folder still names the source's custom connector {CopyTestWorkspace.ConnectorInternalId} instead of its copy.", folder);
        }

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Request(), prepared.Identities, CancellationToken.None);

        Assert.Contains(expected, report.Blockers);
    }

    [Fact]
    public async Task AuditWorkspace_WorkflowCopy_PassesAPreparedCopy()
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var request = workspace.Request();

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(request, (await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None)).Identities, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Empty(report.ComponentSchemaNames);
    }

    [Theory]
    [InlineData("two flows")]
    [InlineData("metadata naming another id")]
    [InlineData("name push refuses")]
    public async Task AuditWorkspace_WorkflowCopy_ReadsTheCopyAsItsFirstDeployWill(string damage)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var request = workspace.Request();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);
        var workflowCopy = Destination(prepared, CopyIdentityKinds.Workflow);
        var workflowFolder = workspace.CopiedWorkflowFolder(prepared);
        var metadataPath = workflowFolder + "/metadata.yml";
        var secondFolder = WorkflowWorkspace.RelativeFolderFor("Second", SecondWorkflow);

        switch (damage)
        {
            case "two flows":
                workspace.Write(secondFolder + "/workflow.json", workspace.Read(workflowFolder + "/workflow.json"));
                workspace.Write(secondFolder + "/metadata.yml", workspace.Read(metadataPath).Replace(workflowCopy, SecondWorkflow.ToString("D"), StringComparison.Ordinal));
                break;
            case "metadata naming another id":
                workspace.Write(metadataPath, workspace.Read(metadataPath).Replace($"workflowId: {workflowCopy}", $"workflowId: {SecondWorkflow:D}", StringComparison.Ordinal), preamble: true);
                break;
            case "name push refuses":
                workspace.Write(metadataPath, workspace.Read(metadataPath).Replace($"name: {CopyTestWorkspace.StandaloneWorkflowCopyName}\n", $"name: {new string('n', WorkflowNaming.MaximumLength + 1)}\n", StringComparison.Ordinal), preamble: true);
                break;
        }

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(request, prepared.Identities, CancellationToken.None);

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.Unreadable, await workspace.PushReaderFailureAsync()), Assert.Single(report.Blockers));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("declaration")]
    public async Task AuditWorkspace_WorkflowCopy_FindsALeftoverSourceReference(string place)
    {
        using var workspace = CopyTestWorkspace.StandaloneWorkflow();
        var request = workspace.Request();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);
        var referenceCopy = prepared.Identities.Single(mapping => mapping.Source == CopyTestWorkspace.HexSuffixedReference).Destination;
        var file = place == "metadata" ? workspace.CopiedWorkflowFolder(prepared) + "/metadata.yml" : $"infrastructure/connections/{referenceCopy}.sync.yaml";
        workspace.Write(file, workspace.Read(file).Replace(referenceCopy, CopyTestWorkspace.HexSuffixedReference, StringComparison.Ordinal), preamble: workspace.HasPreamble(file));

        var finding = Assert.Single((await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(request, prepared.Identities, CancellationToken.None)).Blockers);
        Assert.Equal(CopyAuditCodes.LeftoverSourceIdentity, finding.Code);
        Assert.Equal(file, finding.Path);
        Assert.EndsWith($" still names the source's connection reference {CopyTestWorkspace.HexSuffixedReference}.", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CopyWorkspaceKinds.Agent)]
    [InlineData(CopyWorkspaceKinds.Workflow)]
    public async Task AuditWorkspace_FindsAFolderThatStillCarriesTheSourceWorkflowId(string kind)
    {
        using var workspace = kind == CopyWorkspaceKinds.Workflow ? CopyTestWorkspace.StandaloneWorkflow() : CopyTestWorkspace.Cli();
        var request = workspace.Request();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);
        var workflow = prepared.Identities.Single(mapping => mapping.Kind == CopyIdentityKinds.Workflow);
        var workflowFolder = workspace.CopiedWorkflowFolder(prepared);
        var leftoverFolder = workflowFolder.Replace(workflow.Destination, workflow.Source, StringComparison.Ordinal);
        Directory.Move(workspace.Path(workflowFolder), workspace.Path(leftoverFolder));
        workspace.Write(leftoverFolder + "/metadata.yml", workspace.Read(leftoverFolder + "/metadata.yml").Replace(workflow.Destination, workflow.Source, StringComparison.Ordinal), preamble: true);

        Assert.Contains(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The workflow folder still names the source's workflow {workflow.Source} instead of its copy.", leftoverFolder), (await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(request, prepared.Identities, CancellationToken.None)).Blockers);
    }

    [Theory]
    [InlineData(CopyWorkspaceKinds.Agent)]
    [InlineData(CopyWorkspaceKinds.Workflow)]
    public async Task AuditWorkspace_FindsASourceReferenceLeftInAWorkflowDefinition(string kind)
    {
        using var workspace = kind == CopyWorkspaceKinds.Workflow ? CopyTestWorkspace.StandaloneWorkflow() : CopyTestWorkspace.Cli();
        var request = workspace.Request();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(request, CancellationToken.None);
        var reference = prepared.Identities.First(mapping => mapping.Kind == CopyIdentityKinds.ConnectionReference);
        var definitionPath = workspace.CopiedWorkflowFolder(prepared) + "/workflow.json";
        workspace.Write(definitionPath, workspace.Read(definitionPath).Replace($"\"{reference.Destination}\"", $"\"{reference.Source}\"", StringComparison.Ordinal), preamble: true);

        Assert.Equal(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The workflow definition still names the source's connection reference {reference.Source} instead of its copy.", definitionPath), Assert.Single((await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(request, prepared.Identities, CancellationToken.None)).Blockers));
    }

    [Fact]
    public async Task ProposeSchemaName_KeepsThePublisherPrefixAndEndsWithSixRandomLetters()
    {
        using var workspace = CopyTestWorkspace.Classic();

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, CopyTestWorkspace.ClassicSchema, CopyTestWorkspace.DestinationDisplayName, CancellationToken.None);

        Assert.Matches("^cr2_teamcopy_[A-Za-z]{6}$", proposal);
    }

    [Fact]
    public async Task ProposeSchemaName_ReturnsTheRoomTheLongestComponentLeaves()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var topicName = new string('t', 70);
        workspace.Write($"topics/{topicName}.mcs.yml", workspace.Read("topics/Greeting.mcs.yml"));

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, CopyTestWorkspace.ClassicSchema, "A very long display name for the copy", CancellationToken.None);

        Assert.Matches("^cr2_averylongdis_[A-Za-z]{6}$", proposal);
        Assert.Equal(100, (proposal + ".topic." + topicName).Length);
    }

    [Fact]
    public async Task ProposeSchemaName_IgnoresComponentsNotRootedAtTheSource()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write($"knowledge/cr9_other.topic.{new string('k', 80)}.mcs.yml", "mcs.metadata:\n  componentName: Foreign\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/foreign\n");

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, CopyTestWorkspace.ClassicSchema, "A very long display name for the copy", CancellationToken.None);

        Assert.Matches("^cr2_averylongdisplaynamef_[A-Za-z]{6}$", proposal);
    }

    [Fact]
    public async Task ProposeSchemaName_LeavesRoomForACliKnowledgeFile()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var stem = new string('k', 70);
        workspace.Write("capabilities/knowledge/files/Policies.txt", "Policies content\n");
        workspace.Write($"capabilities/knowledge/files/{stem}.mcs.yml", "mcs.metadata:\n  componentName: Policies.txt\nkind: FileAttachmentComponentMetadata\n");

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, CopyTestWorkspace.CliSchema, "A very long display name for the copy", CancellationToken.None);

        Assert.Matches("^cr1_averylongdisp_[A-Za-z]{6}$", proposal);
        Assert.Equal(100, (proposal + ".file." + stem).Length);
    }

    [Fact]
    public async Task ProposeSchemaName_ALongSourceSchema_AllowsAsLongAName()
    {
        using var workspace = CopyTestWorkspace.Classic();

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, "cr2_" + new string('s', 36), "A very long display name for the copy", CancellationToken.None);

        Assert.Matches("^cr2_averylongdisplaynameforthecop_[A-Za-z]{6}$", proposal);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("9x_agent")]
    public async Task ProposeSchemaName_ASourceWithoutAValidPublisherPrefix_UsesCr(string sourceSchemaName)
    {
        using var workspace = CopyTestWorkspace.Classic();

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, sourceSchemaName, CopyTestWorkspace.DestinationDisplayName, CancellationToken.None);

        Assert.Matches("^cr_teamcopy_[A-Za-z]{6}$", proposal);
    }

    [Fact]
    public async Task ProposeSchemaName_AnUnreadableWorkspace_CountsNoComponents()
    {
        using var workspace = CopyTestWorkspace.Classic();
        workspace.Write($"topics/{new string('t', 95)}.mcs.yml", workspace.Read("topics/Greeting.mcs.yml"));
        workspace.Delete("settings.mcs.yml");

        var proposal = await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, CopyTestWorkspace.ClassicSchema, "A very long display name for the copy", CancellationToken.None);

        Assert.Matches("^cr2_averylongdisplaynamef_[A-Za-z]{6}$", proposal);
    }

    [Theory]
    [InlineData(90, "Team Copy")]
    [InlineData(0, "日本語")]
    public async Task ProposeSchemaName_WhenNoNameFits_ReturnsNull(int topicNameLength, string displayName)
    {
        using var workspace = CopyTestWorkspace.Classic();

        if (topicNameLength > 0)
        {
            workspace.Write($"topics/{new string('t', topicNameLength)}.mcs.yml", workspace.Read("topics/Greeting.mcs.yml"));
        }

        Assert.Null(await CopyTestWorkspace.CreateService().ProposeSchemaNameAsync(workspace.Root, CopyTestWorkspace.ClassicSchema, displayName, CancellationToken.None));
    }

    [Fact]
    public async Task AuditEnvironment_AFreeEnvironment_PassesWithOneQueryPerResourceType()
    {
        var client = CopyTestWorkspace.FreeEnvironment();

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Empty(report.Notices);
        client.Verify(environment => environment.GetAgentIdBySchemaNameAsync(DestinationSchema, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.FindComponentSchemaNamesAsync(DestinationSchema + ".", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.FindExistingWorkflowIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { OwnedWorkflow })), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.FindExistingAIModelIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { OwnedModel })), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.FindExistingConnectorIdsAsync(It.Is<IEnumerable<Guid>>(identifiers => identifiers.SequenceEqual(new[] { OwnedConnector })), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.FindConnectorsByInternalIdPrefixesAsync(It.Is<IEnumerable<string>>(prefixes => prefixes.SequenceEqual(new[] { OwnedInternalIdPrefix })), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.Is<IEnumerable<string>>(names => names.SequenceEqual(new[] { OwnedReference })), It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuditEnvironment_WithoutACopiedConnector_ReadsNoConnectorSuffix()
    {
        var client = CopyTestWorkspace.FreeEnvironment();

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities().Where(mapping => mapping.Kind != CopyIdentityKinds.ConnectorInternalId).ToArray(), client.Object, CancellationToken.None);

        Assert.True(report.Passed);
        client.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(environment => environment.FindConnectorsByInternalIdPrefixesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuditEnvironment_ACopiedConnectorPredictedForAnotherEnvironment_IsBlocked()
    {
        var client = CopyTestWorkspace.FreeEnvironment(environmentSuffix: "c57fa9bfea132312");

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.UnsupportedResource, finding.Code);
        Assert.Contains(OwnedInternalId, finding.Message, StringComparison.Ordinal);
        Assert.Contains("c57fa9bfea132312", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditEnvironment_WithoutAnyCustomConnector_IsUnverifiable()
    {
        var client = CopyTestWorkspace.FreeEnvironment(environmentSuffix: null);

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.UnsupportedResource, finding.Code);
        Assert.Contains("from a workspace connected to this environment", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("share", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditEnvironment_AConnectorAlreadyUsingACopiedConnectorsStablePrefix_IsTaken()
    {
        var holder = new CustomConnectorMetadata { ConnectorId = Guid.NewGuid(), ConnectorInternalId = OwnedInternalId.ToUpperInvariant() };
        var lookalike = new CustomConnectorMetadata { ConnectorId = Guid.NewGuid(), ConnectorInternalId = OwnedInternalIdPrefix + "extra-5fd6c23f2bd6bf7828" };
        var client = CopyTestWorkspace.FreeEnvironment(connectorsWithThePrefix: new[] { holder, lookalike });

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code);
        Assert.Equal($"The custom connector {holder.ConnectorInternalId} ({holder.ConnectorId}) already uses the name a connector this copy needs. Copy the agent again.", finding.Message);
    }

    [Fact]
    public async Task AuditEnvironment_ASchemaHeldByAnotherAgent_IsTakenWithoutProbingItsComponents()
    {
        var holder = Guid.NewGuid();
        var client = CopyTestWorkspace.FreeEnvironment();
        client.Setup(environment => environment.GetAgentIdBySchemaNameAsync(DestinationSchema, It.IsAny<CancellationToken>())).ReturnsAsync(holder);

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code);
        Assert.Equal($"An agent with the schema name {DestinationSchema} already exists in the environment ({holder}). Copy the agent again with another schema name.", finding.Message);
        client.Verify(environment => environment.FindComponentSchemaNamesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuditEnvironment_ComponentsAlreadyNamedUnderTheNewSchema_AreTaken()
    {
        var client = CopyTestWorkspace.FreeEnvironment();
        client.Setup(environment => environment.FindComponentSchemaNamesAsync(DestinationSchema + ".", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { DestinationSchema + ".topic.Greeting" });

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code);
        Assert.Equal($"Components named under {DestinationSchema} already exist in the environment (for example {DestinationSchema}.topic.Greeting). Copy the agent again with another schema name.", finding.Message);
    }

    [Fact]
    public async Task AuditEnvironment_OwnedIdentitiesThatExist_AreTaken()
    {
        var client = CopyTestWorkspace.FreeEnvironment(existingWorkflows: new[] { OwnedWorkflow }, existingModels: new[] { OwnedModel }, existingConnectors: new[] { OwnedConnector }, existingReferences: new[] { OwnedReference });

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(AgentCopyRequest, Identities(), client.Object, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                $"The workflow {OwnedWorkflow} this copy would create already exists in the environment. Copy the agent again.",
                $"The AI prompt {OwnedModel} this copy would create already exists in the environment. Copy the agent again.",
                $"The custom connector {OwnedConnector} this copy would create already exists in the environment. Copy the agent again.",
                $"The connection reference {OwnedReference} this copy would create already exists in the environment. Copy the agent again with another schema name.",
            },
            report.Blockers.Select(finding => finding.Message));
        Assert.All(report.Blockers, finding => Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code));
    }

    private static string Destination(CopyPreparationResult prepared, string kind) => prepared.Identities.Single(mapping => mapping.Kind == kind).Destination;

    private static CopyIdentityMapping[] Identities() => new[]
    {
        new CopyIdentityMapping(CopyIdentityKinds.Schema, "cr1_source", DestinationSchema),
        new CopyIdentityMapping(CopyIdentityKinds.Workflow, Guid.NewGuid().ToString("D"), OwnedWorkflow.ToString("D")),
        new CopyIdentityMapping(CopyIdentityKinds.AIModel, Guid.NewGuid().ToString("D"), OwnedModel.ToString("D")),
        new CopyIdentityMapping(CopyIdentityKinds.Connector, Guid.NewGuid().ToString("D"), OwnedConnector.ToString("D")),
        new CopyIdentityMapping(CopyIdentityKinds.ConnectorInternalId, "shared_cr1-5fweather-20plus-5fc57fa9bfea132312", OwnedInternalId),
        new CopyIdentityMapping(CopyIdentityKinds.ConnectionReference, "cr1_source.cr.shared_teams", OwnedReference),
    };

    private static DialogComponent Topic(string schemaName) => new(schemaName: schemaName, displayName: "Greeting", description: string.Empty, id: Guid.NewGuid(), parentBotComponentId: default, dialog: new AdaptiveDialog());
}
