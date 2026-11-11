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

    [Fact]
    public async Task AuditWorkspace_AComponentCallingTheSourceWorkflowAgain_IsALeftover()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var workflowCopy = Destination(prepared, CopyIdentityKinds.Workflow);
        workspace.Write("actions/ClassicFlow.mcs.yml", workspace.Read("actions/ClassicFlow.mcs.yml").Replace(workflowCopy, CopyTestWorkspace.ClassicWorkflowId.ToString("D"), StringComparison.Ordinal));

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.LeftoverSourceIdentity && finding.Path == metadataPath);
    }

    [Fact]
    public async Task AuditWorkspace_ACallToAnUnknownPrompt_IsUnresolved()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write("actions/Unknown.mcs.yml", "mcs.metadata:\n  componentName: Unknown\nkind: TaskDialog\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: 99999999-2222-3333-4444-555555555555\n\noutputMode: All\n");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.UnresolvedReference && finding.Path == "actions/Unknown.mcs.yml");
    }

    [Fact]
    public async Task AuditWorkspace_AComponentOfAnotherAgent_IsForeign()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write("knowledge/cr9_other.topic.Foreign_abc123.mcs.yml", "mcs.metadata:\n  componentName: Foreign\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/foreign\n");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.ForeignComponentSchema && finding.Message.Contains("cr9_other.topic.Foreign_abc123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuditWorkspace_ASourceComponentLeftBehind_IsALeftover()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write(CopyTestWorkspace.ClassicKnowledgeFile, "mcs.metadata:\n  componentName: Handbook\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/handbook\n");

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.LeftoverSourceIdentity && finding.Message.Contains("cr2_classic.topic.Handbook_abc123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuditWorkspace_ADestinationSchemaOverTheLimit_IsTooLong()
    {
        using var workspace = CopyTestWorkspace.Cli();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var identities = prepared.Identities.Select(mapping => mapping.Kind == CopyIdentityKinds.Schema ? mapping with { Destination = "cr1_" + new string('x', 97) } : mapping).ToArray();

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, identities, CancellationToken.None);

        Assert.Contains(report.Blockers, finding => finding.Code == CopyAuditCodes.SchemaTooLong);
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

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.Unreadable, finding.Code);
        Assert.Contains($"{greeting} 2 times", finding.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { greeting }, report.ComponentSchemaNames.Where(schema => schema.StartsWith(greeting, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("\"\"", "is required")]
    [InlineData("Help <desk>", "contains < or >")]
    [InlineData("A display name far longer than Copilot Studio accepts", "is 53 characters long")]
    public async Task AuditWorkspace_AnAgentNameCopilotStudioRefuses_IsBlocked(string displayName, string problem)
    {
        using var workspace = CopyTestWorkspace.Cli();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        workspace.Write("settings.mcs.yml", workspace.Read("settings.mcs.yml").Replace($"displayName: {CopyTestWorkspace.DestinationDisplayName}\n", $"displayName: {displayName}\n", StringComparison.Ordinal));

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.AgentNameUnsupported, finding.Code);
        Assert.Equal("settings.mcs.yml", finding.Path);
        Assert.StartsWith("The agent's display name " + problem, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditWorkspace_ACopiedFlowStillRunningTheSourcesPrompt_IsBlocked()
    {
        using var workspace = CopyTestWorkspace.Classic();
        var prepared = await CopyTestWorkspace.CreateService().PrepareAsync(workspace.Request(), CancellationToken.None);
        var workflowFolder = "workflows/" + workspace.Folder("workflows", Guid.Parse(Destination(prepared, CopyIdentityKinds.Workflow)));
        workspace.AddPromptRun(workflowFolder, CopyTestWorkspace.PromptModelId);

        var report = await CopyTestWorkspace.CreateAuditor().AuditWorkspaceAsync(workspace.Root, prepared.Identities, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.LeftoverSourceIdentity, finding.Code);
        Assert.Equal(workflowFolder + "/workflow.json", finding.Path);
        Assert.Contains(CopyTestWorkspace.PromptModelId.ToString("D"), finding.Message, StringComparison.Ordinal);
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

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities().Where(mapping => mapping.Kind != CopyIdentityKinds.ConnectorInternalId).ToArray(), client.Object, CancellationToken.None);

        Assert.True(report.Passed);
        client.Verify(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(environment => environment.FindConnectorsByInternalIdPrefixesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuditEnvironment_ACopiedConnectorPredictedForAnotherEnvironment_IsBlocked()
    {
        var client = CopyTestWorkspace.FreeEnvironment(environmentSuffix: "c57fa9bfea132312");

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.UnsupportedResource, finding.Code);
        Assert.Contains(OwnedInternalId, finding.Message, StringComparison.Ordinal);
        Assert.Contains("c57fa9bfea132312", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditEnvironment_WithoutAnyCustomConnector_IsUnverifiable()
    {
        var client = CopyTestWorkspace.FreeEnvironment(environmentSuffix: null);

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

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

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

        var finding = Assert.Single(report.Blockers);
        Assert.Equal(CopyAuditCodes.IdentityTaken, finding.Code);
        Assert.Equal($"Components named under {DestinationSchema} already exist in the environment (for example {DestinationSchema}.topic.Greeting). Copy the agent again with another schema name.", finding.Message);
    }

    [Fact]
    public async Task AuditEnvironment_OwnedIdentitiesThatExist_AreTaken()
    {
        var client = CopyTestWorkspace.FreeEnvironment(existingWorkflows: new[] { OwnedWorkflow }, existingModels: new[] { OwnedModel }, existingConnectors: new[] { OwnedConnector }, existingReferences: new[] { OwnedReference });

        var report = await CopyTestWorkspace.CreateAuditor().AuditEnvironmentAsync(Identities(), client.Object, CancellationToken.None);

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
