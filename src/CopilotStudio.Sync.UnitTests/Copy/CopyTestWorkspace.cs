using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Copy;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

internal sealed class CopyTestWorkspace : IDisposable
{
    public const string ClassicSchema = "cr2_classic";
    public const string CliSchema = "cr1_cli";
    public const string ClassicTeamsReference = "cr2_classic.shared_teams.0add195409434636943683365d5bbcce";
    public const string ClassicWeatherReference = "new_sharedmsnweather_08889";
    public const string CliTeamsReference = "cr1_cli.cr.shared_teams";
    public const string CliWeatherReference = "new_sharedmsnweather_1234abcd";
    public const string ClassicWorkflowFolder = "workflows/ClassicFlow-94945b98-fe8c-f111-8077-7ced8d702753";
    public const string CliWorkflowFolder = "workflows/CliFlow-6e5229e3-e343-f111-88b5-7ced8d3b6119";
    public const string PromptFolder = "prompts/promptone-3b5436b4-d7b4-4389-96e8-107446c9094a";
    public const string ClassicKnowledgeFile = "knowledge/cr2_classic.topic.Handbook_abc123.mcs.yml";
    public const string CliKnowledgeFile = "capabilities/knowledge/cr1_cli.Handbook_xyz789.mcs.yml";
    public const string DestinationDisplayName = "Team Copy";
    public const string ConnectorName = "cr1_5Fweather-20plus";
    public const string ConnectorInternalId = "shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd";
    public const string ConnectorReference = "cr1_cli.cr.shared_cr1-5fweather-20plus-5f566c5f3d9705a6dd.0b1c2d3e4f5a46b7c8d9e0f1a2b3c4d5";
    public const string ConnectorFolder = "connectors/cr1_5Fweather-20plus-7c1d3e5f-9a2b-4c6d-8e0f-1a3b5c7d9e2f";
    public const string ConnectorToolFile = "capabilities/tools/Getforecast_q7R.mcs.yml";
    public const string TeamsToolFile = "capabilities/tools/Getasection_fcU.mcs.yml";
    public const string StandaloneWorkflowName = "Approve expenses";
    public const string StandaloneWorkflowCopyName = "Approve expenses copy";
    public const string AgentRootedReference = "cre98_AgentA1.shared_commondataserviceforapps.787379aebbb2450a96f20e20d71bfd30";
    public const string HexSuffixedReference = "new_sharedcommondataserviceforapps_275f6bca";
    public const string UnsuffixedReference = "new_sharedoffice365";

    public static readonly Guid ClassicWorkflowId = Guid.Parse("94945b98-fe8c-f111-8077-7ced8d702753");
    public static readonly Guid CliWorkflowId = Guid.Parse("6e5229e3-e343-f111-88b5-7ced8d3b6119");
    public static readonly Guid PromptModelId = Guid.Parse("3b5436b4-d7b4-4389-96e8-107446c9094a");
    public static readonly Guid ConnectorId = Guid.Parse("7c1d3e5f-9a2b-4c6d-8e0f-1a3b5c7d9e2f");
    public static readonly Guid StandaloneWorkflowId = Guid.Parse("b6cf03a8-d3ee-5321-a464-e415cfc53b75");
    public static readonly Guid CalledPromptId = Guid.Parse("775d9f81-18d5-587f-abf4-ff8f9f744804");
    public static readonly Guid ChildWorkflowId = Guid.Parse("5d2b7e1c-8a4f-4c3e-9b6d-0f1e2a3b4c5d");
    public static readonly string StandaloneWorkflowFolder = WorkflowWorkspace.RelativeFolderFor(StandaloneWorkflowName, StandaloneWorkflowId);

    private CopyTestWorkspace(string root, string sourceSchema, string kind = CopyWorkspaceKinds.Agent)
    {
        Root = root;
        SourceSchema = sourceSchema;
        Kind = kind;
    }

    public string Root { get; }

    public string SourceSchema { get; }

    public string Kind { get; }

    public static CopyTestWorkspace Classic()
    {
        var workspace = new CopyTestWorkspace(NewRoot(), ClassicSchema);
        workspace.Write("settings.mcs.yml", "displayName: Classic Source\nschemaName: cr2_classic\naccessControlPolicy: GroupMembership\nauthenticationMode: Integrated\nauthenticationTrigger: Always\nconfiguration:\n  gPTSettings:\n    defaultSchemaName: cr2_classic.gpt.default\n\n  recognizer:\n    kind: GenerativeAIRecognizer\n\npublishedOn: 2026-09-10T15:49:54.0000000Z\ntemplate: default-2.1.0\nlanguage: 1033\n");
        workspace.Write("agent.mcs.yml", "mcs.metadata:\n  componentName: Classic Source\nkind: GptComponentMetadata\ninstructions: Help employees with cr2_classic questions.\ngptCapabilities: {}\n");
        workspace.Write("topics/Greeting.mcs.yml", "mcs.metadata:\n  componentName: Greeting\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    triggerQueries:\n      - Hello\n\n  actions:\n    - kind: SendActivity\n      id: sendMessage_abmysR\n      activity: Welcome to cr2_classic support.\n");
        workspace.Write("actions/ClassicFlow.mcs.yml", "mcs.metadata:\n  componentName: Classic Flow\nkind: TaskDialog\naction:\n  kind: InvokeFlowTaskAction\n  flowId: 94945b98-fe8c-f111-8077-7ced8d702753\n\noutputMode: All\n");
        workspace.Write("actions/promptone.mcs.yml", "mcs.metadata:\n  componentName: prompt one\nkind: TaskDialog\nmodelDisplayName: prompt one\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\n\noutputMode: All\n");
        workspace.Write("actions/MicrosoftTeams-Createachat.mcs.yml", "mcs.metadata:\n  componentName: Microsoft Teams - Create a chat\nkind: TaskDialog\nmodelDisplayName: Create a chat\noutputs:\n  - propertyName: @odata.etag\n\naction:\n  kind: InvokeConnectorTaskAction\n  connectionReference: 'cr2_classic.shared_teams.0add195409434636943683365d5bbcce'\n  operationId: CreateChat\n\noutputMode: All\n");
        workspace.Write(ClassicKnowledgeFile, "mcs.metadata:\n  componentName: Handbook\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/handbook\n");
        workspace.Write("connectionreferences.mcs.yml", "connectionReferences:\n  - connectionReferenceLogicalName: cr2_classic.shared_teams.0add195409434636943683365d5bbcce\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n  - connectionReferenceLogicalName: new_sharedmsnweather_08889\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather\n", preamble: true);
        workspace.WriteWorkflow(ClassicWorkflowFolder, ClassicWorkflowId, "Classic Flow", ClassicTeamsReference, ClassicWeatherReference);
        workspace.WritePrompt();

        return workspace;
    }

    public static CopyTestWorkspace Cli()
    {
        var workspace = new CopyTestWorkspace(NewRoot(), CliSchema);
        workspace.Write("agent.sync.yaml", "layoutVersion: 1\n");
        workspace.Write("settings.mcs.yml", "displayName: Cli Source\nschemaName: cr1_cli\naccessControlPolicy: GroupMembership\nauthenticationMode: Integrated\nauthenticationTrigger: Always\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\n\npublishedOn: 2026-08-20T23:11:43.0000000Z\ntemplate: cliagent-1.0.0\nlanguage: 1033\n");
        workspace.Write("capabilities/tools/CliFlow_kvb.mcs.yml", "mcs.metadata:\n  componentName: Cli Flow\n  description: Calls the flow.\nkind: WorkflowTool\nworkflowId: 6e5229e3-e343-f111-88b5-7ced8d3b6119\nconnectionProperties:\n  $kind: ConnectionProperties\n  mode: Invoker\n");
        workspace.Write(TeamsToolFile, "mcs.metadata:\n  componentName: Get a section\n  description: Gets a section.\nkind: ConnectorTool\nauthMode: Invoker\nconnectionReference: \"cr1_cli.cr.shared_teams\"\nconnectorId: /providers/Microsoft.PowerApps/apis/shared_teams\noperationId: GetSection\n");
        workspace.Write(CliKnowledgeFile, "mcs.metadata:\n  componentName: Handbook\nkind: KnowledgeSourceConfiguration\nsource:\n  kind: SharePointSearchSource\n  site: https://contoso.sharepoint.com/sites/handbook\n");
        workspace.Write("infrastructure/connections/cr1_cli.cr.shared_teams.sync.yaml", "connectionReferences:\n  - connectionReferenceLogicalName: cr1_cli.cr.shared_teams\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n");
        workspace.Write("infrastructure/connections/new_sharedmsnweather_1234abcd.sync.yaml", "connectionReferences:\n  - connectionReferenceLogicalName: new_sharedmsnweather_1234abcd\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather\n");
        workspace.WriteWorkflow(CliWorkflowFolder, CliWorkflowId, "Cli Flow", CliTeamsReference, CliWeatherReference);

        return workspace;
    }

    public static CopyTestWorkspace StandaloneWorkflow()
    {
        var workspace = new CopyTestWorkspace(NewRoot(), string.Empty, CopyWorkspaceKinds.Workflow);
        workspace.Write(AgentClassifier.WorkspaceLayoutMarkerFileName, AgentClassifier.BuildWorkspaceLayoutMarker());
        workspace.Write(WorkflowWorkspace.ProjectFileName, WorkflowWorkspace.BuildProjectFile());
        workspace.Write(StandaloneWorkflowFolder + "/metadata.yml", $"jsonFileName: {StandaloneWorkflowFolder}/workflow.json\nworkflowId: {StandaloneWorkflowId:D}\nname: {StandaloneWorkflowName}\ntype: 1\ndescription: Routes each expense report to its approver.\nsubprocess: false\ncategory: 5\nmode: 0\nscope: 4\nonDemand: false\ntriggerOnCreate: false\ntriggerOnDelete: false\nasyncAutodelete: false\nsyncWorkflowLogOnFailure: false\nstateCode: 1\nstatusCode: 2\nrunAs: 1\nisTransacted: true\nintroducedVersion: 1.0\nisCustomizable:\n  value: true\n  canBeChanged: true\n  managedPropertyLogicalName: iscustomizableanddeletable\nbusinessProcessType: 0\nisCustomProcessingStepAllowedForOtherPublishers:\n  value: true\n  canBeChanged: true\n  managedPropertyLogicalName: canbedeleted\nmodernFlowType: 1\nprimaryEntity: none\nconnectionReferences:\n- {AgentRootedReference}\n- {HexSuffixedReference}\n- {UnsuffixedReference}\n", preamble: true);
        workspace.Write(StandaloneWorkflowFolder + "/workflow.json", "{\n  \"properties\": {\n    \"connectionReferences\": {\n      \"shared_commondataserviceforapps\": {\n        \"api\": {\n          \"name\": \"shared_commondataserviceforapps\"\n        },\n        \"connection\": {\n          \"connectionReferenceLogicalName\": \"" + AgentRootedReference + "\"\n        },\n        \"runtimeSource\": \"embedded\"\n      },\n      \"shared_commondataserviceforapps_1\": {\n        \"api\": {\n          \"name\": \"shared_commondataserviceforapps\"\n        },\n        \"connection\": {\n          \"connectionReferenceLogicalName\": \"" + HexSuffixedReference + "\"\n        },\n        \"runtimeSource\": \"embedded\"\n      },\n      \"shared_office365\": {\n        \"api\": {\n          \"name\": \"shared_office365\"\n        },\n        \"connection\": {\n          \"connectionReferenceLogicalName\": \"" + UnsuffixedReference + "\"\n        },\n        \"runtimeSource\": \"embedded\"\n      }\n    },\n    \"definition\": {\n      \"contentVersion\": \"1.0.0.0\",\n      \"triggers\": {\n        \"manual\": {\n          \"type\": \"Request\",\n          \"kind\": \"Button\"\n        }\n      },\n      \"actions\": {\n        \"Run_a_prompt\": {\n          \"type\": \"OpenApiConnection\",\n          \"inputs\": {\n            \"parameters\": {\n              \"recordId\": \"" + CalledPromptId.ToString("D") + "\"\n            },\n            \"host\": {\n              \"apiId\": \"providers/Microsoft.ProcessSimple/operationGroups/aibuilder\",\n              \"operationId\": \"aibuilderpredict_customprompt\"\n            }\n          }\n        },\n        \"Run_a_Child_Flow\": {\n          \"type\": \"Workflow\",\n          \"inputs\": {\n            \"host\": {\n              \"workflowReferenceName\": \"" + ChildWorkflowId.ToString("D") + "\"\n            }\n          },\n          \"runAfter\": {\n            \"Run_a_prompt\": [\n              \"Succeeded\"\n            ]\n          }\n        }\n      }\n    }\n  },\n  \"schemaVersion\": \"1.0.0.0\"\n}\n", preamble: true);
        workspace.Write($"infrastructure/connections/{AgentRootedReference}.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {AgentRootedReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps\n");
        workspace.Write($"infrastructure/connections/{HexSuffixedReference}.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {HexSuffixedReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps\n", preamble: true);

        return workspace;
    }

    public static WorkspaceSynchronizer CreateSynchronizer() => new(new SyncMcsFileParser(LspProjectorService.Instance), new FileAccessorFactory(), new Mock<IIslandControlPlaneService>(MockBehavior.Strict).Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());

    public static IWorkspaceCopyService CreateService() => new WorkspaceCopyService(CreateSynchronizer());

    public static CopyIdentityAuditor CreateAuditor() => new(CreateSynchronizer());

    public static Mock<ICopyDataverseClient> FreeEnvironment(Guid[]? existingWorkflows = null, Guid[]? existingModels = null, Guid[]? existingConnectors = null, string[]? existingReferences = null, string? environmentSuffix = "d6c23f2bd6bf7828", CustomConnectorMetadata[]? connectorsWithThePrefix = null, SyncDataverseClient.WorkflowSummary[]? cloudFlowsWithTheName = null)
    {
        var workflows = new HashSet<Guid>(existingWorkflows ?? Array.Empty<Guid>());
        var models = new HashSet<Guid>(existingModels ?? Array.Empty<Guid>());
        var connectors = new HashSet<Guid>(existingConnectors ?? Array.Empty<Guid>());
        var references = new HashSet<string>(existingReferences ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var client = new Mock<ICopyDataverseClient>(MockBehavior.Strict);
        client.Setup(environment => environment.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.Empty);
        client.Setup(environment => environment.FindComponentSchemaNamesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        client.Setup(environment => environment.FindExistingWorkflowIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<Guid> identifiers, CancellationToken _) => identifiers.Where(workflows.Contains).ToArray());
        client.Setup(environment => environment.FindExistingAIModelIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<Guid> identifiers, CancellationToken _) => identifiers.Where(models.Contains).ToArray());
        client.Setup(environment => environment.FindExistingConnectorIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<Guid> identifiers, CancellationToken _) => identifiers.Where(connectors.Contains).ToArray());
        client.Setup(environment => environment.FindConnectorEnvironmentSuffixAsync(It.IsAny<CancellationToken>())).ReturnsAsync(environmentSuffix);
        client.Setup(environment => environment.FindConnectorsByInternalIdPrefixesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(connectorsWithThePrefix ?? Array.Empty<CustomConnectorMetadata>());
        client.Setup(environment => environment.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IEnumerable<string> names, CancellationToken _) => names.Where(references.Contains).Select(name => new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = name }).ToArray());
        client.Setup(environment => environment.FindCloudFlowsNamedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(cloudFlowsWithTheName ?? Array.Empty<SyncDataverseClient.WorkflowSummary>());

        return client;
    }

    public CopyPreparationRequest Request(string? destinationSchema = null, string? sourceDefinitionPath = null) => Kind == CopyWorkspaceKinds.Workflow ? new CopyPreparationRequest
    {
        WorkspacePath = Root,
        WorkspaceKind = CopyWorkspaceKinds.Workflow,
        SourceWorkflowId = StandaloneWorkflowId,
        DisplayName = StandaloneWorkflowCopyName,
        CapturedInTargetEnvironment = true,
    } : new CopyPreparationRequest
    {
        WorkspacePath = Root,
        SourceSchemaName = SourceSchema,
        DisplayName = DestinationDisplayName,
        SchemaName = destinationSchema ?? DestinationSchemaFor(SourceSchema),
        SourceDefinitionPath = sourceDefinitionPath,
        CapturedInTargetEnvironment = true,
    };

    public static string DestinationSchemaFor(string sourceSchema) => sourceSchema.Substring(0, sourceSchema.IndexOf('_')) + "_teamcopy_a1b2c3";

    public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public bool Exists(string relative) => File.Exists(Path(relative)) || Directory.Exists(Path(relative));

    public string Read(string relative) => File.ReadAllText(Path(relative), Encoding.UTF8);

    public bool HasPreamble(string relative)
    {
        var bytes = File.ReadAllBytes(Path(relative));

        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    public void Write(string relative, string content, bool preamble = false)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: preamble));
    }

    public void Delete(string relative)
    {
        if (Directory.Exists(Path(relative)))
        {
            Directory.Delete(Path(relative), recursive: true);
        }
        else
        {
            File.Delete(Path(relative));
        }
    }

    public string Folder(string parent, Guid identifier) => Directory.EnumerateDirectories(Path(parent)).Select(System.IO.Path.GetFileName).OfType<string>().Single(name => name.EndsWith(identifier.ToString("D"), StringComparison.OrdinalIgnoreCase));

    public string CopiedWorkflowFolder(CopyPreparationResult result) => "workflows/" + Folder("workflows", Guid.Parse(result.Identities.Single(mapping => mapping.Kind == CopyIdentityKinds.Workflow).Destination));

    public IReadOnlyList<string> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(path => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/') + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).OrderBy(entry => entry, StringComparer.Ordinal).ToArray();

    public async Task<string> PushReaderFailureAsync() => (await Xunit.Assert.ThrowsAsync<StandaloneWorkflowException>(() => StandaloneWorkflowSynchronizer.ReadFolderAsync(WorkflowWorkspace.OpenWorkspace(Root), CancellationToken.None))).Message;

    public void AddPromptRun(string workflowFolder, Guid modelId) => AddAction(workflowFolder, "\"Run_a_prompt\": {\n          \"type\": \"OpenApiConnection\",\n          \"inputs\": {\n            \"parameters\": {\n              \"recordId\": \"" + modelId.ToString("D") + "\"\n            },\n            \"host\": {\n              \"apiId\": \"providers/Microsoft.ProcessSimple/operationGroups/aibuilder\",\n              \"operationId\": \"aibuilderpredict_customprompt\"\n            }\n          }\n        }");

    public void AddChildFlowRun(string workflowFolder, Guid childWorkflowId) => AddAction(workflowFolder, "\"Run_a_Child_Flow\": {\n          \"type\": \"Workflow\",\n          \"inputs\": {\n            \"host\": {\n              \"workflowReferenceName\": \"" + childWorkflowId.ToString("D") + "\"\n            }\n          }\n        }");

    public void WriteConnector(string internalId = ConnectorInternalId, string displayName = "Weather Plus", long? version = null)
    {
        WriteConnectorFolder(ConnectorFolder, ConnectorId, ConnectorName, displayName, internalId, "weather.example.com", version);
        Write($"infrastructure/connections/{ConnectorReference}.sync.yaml", $"connectionReferences:\n  - connectionReferenceLogicalName: {ConnectorReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/{internalId}\n");
        Write(ConnectorToolFile, $"mcs.metadata:\n  componentName: Get forecast\n  description: Gets the forecast.\nkind: ConnectorTool\nauthMode: Invoker\nconnectionReference: \"{ConnectorReference}\"\nconnectorId: /providers/Microsoft.PowerApps/apis/{internalId}\noperationId: GetForecast\n");
    }

    public void WriteConnectorFolder(string folder, Guid connectorId, string name, string displayName, string internalId, string host, long? version)
    {
        Write(folder + "/metadata.yml", "{\n  \"connectorid\": \"" + connectorId.ToString("D") + "\",\n  \"name\": \"" + name + "\",\n  \"displayname\": \"" + displayName + "\",\n  \"connectorinternalid\": \"" + internalId + "\",\n  \"openapidefinition\": \"" + folder + "/openapidefinition.json\",\n  \"connectionparameters\": \"" + folder + "/connectionparameters.json\",\n  \"connectortype\": 1" + (version is { } number ? ",\n  \"versionnumber\": " + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty) + "\n}");
        Write(folder + "/openapidefinition.json", "{\n  \"swagger\": \"2.0\",\n  \"host\": \"" + host + "\"\n}");
        Write(folder + "/connectionparameters.json", "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string NewRoot()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copytests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return root;
    }

    public void WriteWorkflow(string folder, Guid workflowId, string name, string dualUseReference, string workflowOnlyReference)
    {
        Write(folder + "/metadata.yml", $"jsonFileName: {folder}/workflow.json\nworkflowId: {workflowId:D}\nname: {name}\ntype: 1\ncategory: 5\nstateCode: 1\nstatusCode: 2\nmodernFlowType: 1\nprimaryEntity: none\nconnectionReferences:\n- {dualUseReference}\n- {workflowOnlyReference}\n", preamble: true);
        Write(folder + "/workflow.json", "{\n  \"properties\": {\n    \"connectionReferences\": {\n      \"shared_teams\": {\n        \"api\": {\n          \"name\": \"shared_teams\"\n        },\n        \"connection\": {\n          \"connectionReferenceLogicalName\": \"" + dualUseReference + "\"\n        },\n        \"runtimeSource\": \"invoker\"\n      },\n      \"shared_msnweather\": {\n        \"api\": {\n          \"name\": \"shared_msnweather\"\n        },\n        \"connection\": {\n          \"connectionReferenceLogicalName\": \"" + workflowOnlyReference + "\"\n        },\n        \"runtimeSource\": \"invoker\"\n      }\n    },\n    \"definition\": {\n      \"contentVersion\": \"1.0.0.0\",\n      \"triggers\": {\n        \"manual\": {\n          \"type\": \"Request\",\n          \"kind\": \"Skills\"\n        }\n      },\n      \"actions\": {}\n    }\n  },\n  \"schemaVersion\": \"1.0.0.0\"\n}\n", preamble: true);
    }

    private void AddAction(string workflowFolder, string action) => Write(workflowFolder + "/workflow.json", Read(workflowFolder + "/workflow.json").Replace("\"actions\": {}", "\"actions\": {\n        " + action + "\n      }", StringComparison.Ordinal), preamble: true);

    private void WritePrompt()
    {
        Write(PromptFolder + "/metadata.yml", "aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\nname: prompt one\ntemplateId: edfdb190-3791-45d8-9a6c-8f90a37c278a\n", preamble: true);
        Write(PromptFolder + "/prompt.json", "{\n  \"name\": \"prompt one\",\n  \"instruction\": \"Summarize the text.\"\n}\n");
    }
}
