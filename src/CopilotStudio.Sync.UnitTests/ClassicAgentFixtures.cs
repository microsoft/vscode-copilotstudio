// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.Sync.Hosting;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal static class ClassicAgentFixtures
{
    internal const string AgentD1Schema = "crf9a_AgentD1";

    internal const string Cc1Schema = "cr8e9_S5D2C1";

    internal const string Cc1CollectionSchema = "bot_componentcollection_s_5_collection_1";

    internal const string WeatherConnectorReference = AgentD1Schema + ".shared_cre98-5fweather-20agent-202-5f2c520b622e718fe6.c712ea8cc54c478cb088f5d8081b15d6";

    internal const string TeamsReference = AgentD1Schema + ".shared_teams.0add195409434636943683365d5bbcce";

    internal const string MsnWeatherReference = "new_sharedmsnweather_08889";

    internal static readonly Guid FlowId = new("94945b98-fe8c-f111-8077-7ced8d702753");

    internal static readonly Guid ConnectorId = new("a1ac675b-8b84-416e-9450-add1b0db8624");

    internal static readonly Guid SummarizePromptId = new("5995fe80-b68f-4366-9a9c-0cb06f141fed");

    internal static readonly Guid ChildPromptId = new("3b5436b4-d7b4-4389-96e8-107446c9094a");

    internal static readonly byte[] Icon = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89 };

    internal static readonly byte[] RevisedIcon = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x08, 0x06, 0x00, 0x00, 0x00, 0x72, 0xB6, 0x0D, 0x24 };

    private const string ConnectorFolder = "connectors/cre98_5Fweather-20agent-202-a1ac675b-8b84-416e-9450-add1b0db8624";

    internal static string[] AgentD1SchemaNames => new[]
    {
        $"{AgentD1Schema}.action.AgentSDK4Flow1",
        $"{AgentD1Schema}.action.MicrosoftTeams-Createachat",
        $"{AgentD1Schema}.action.WeatherAgent2-WeatherAgent2",
        $"{AgentD1Schema}.action.promptchild1",
        $"{AgentD1Schema}.action.summarizedocument1",
        $"{AgentD1Schema}.agent.Agent",
        $"{AgentD1Schema}.file.File3.txt_NbU",
        $"{AgentD1Schema}.file.abcmain2.txt_0Sh",
        $"{AgentD1Schema}.gpt.default",
        $"{AgentD1Schema}.topic.Escalate",
        $"{AgentD1Schema}.topic.Fallback",
        $"{AgentD1Schema}.topic.Greeting",
        $"{AgentD1Schema}.topic.ThankYou",
    };

    internal static AgentWorkspaceSnapshot AgentD1(string folder) => Snapshot(folder, new Dictionary<string, object>
    {
        ["settings.mcs.yml"] = Settings("Agent D1", AgentD1Schema),
        ["agent.mcs.yml"] = "mcs.metadata:\n  componentName: Agent D1\nkind: GptComponentMetadata\ninstructions:\ngptCapabilities: {}\n",
        ["connectionreferences.mcs.yml"] = $"connectionReferences:\n  - connectionReferenceLogicalName: {WeatherConnectorReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_cre98-5fweather-20agent-202-5f2c520b622e718fe6\n  - connectionReferenceLogicalName: {TeamsReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n  - connectionReferenceLogicalName: {MsnWeatherReference}\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather\n",
        ["icon.png"] = Icon,
        ["actions/AgentSDK4Flow1.mcs.yml"] = $"mcs.metadata:\n  componentName: AgentSDK4 Flow1\nkind: TaskDialog\noutputs:\n  - propertyName: textout1\n\naction:\n  kind: InvokeFlowTaskAction\n  flowId: {FlowId}\n  connectionProperties:\n    mode: Invoker\n\noutputMode: All\n",
        ["actions/MicrosoftTeams-Createachat.mcs.yml"] = $"mcs.metadata:\n  componentName: Microsoft Teams - Create a chat\nkind: TaskDialog\nmodelDisplayName: Create a chat\nmodelDescription: Creates a one on one or group chat\noutputs:\n  - propertyName: id\n\naction:\n  kind: InvokeConnectorTaskAction\n  connectionReference: {TeamsReference}\n  connectionProperties:\n    mode: Invoker\n\n  operationId: CreateChat\n\noutputMode: All\n",
        ["actions/summarizedocument1.mcs.yml"] = PromptAction("summarize document 1", "Text_20input", SummarizePromptId),
        ["actions/WeatherAgent2-WeatherAgent2.mcs.yml"] = $"mcs.metadata:\n  componentName: Weather Agent 2 - Weather Agent 2\nkind: TaskDialog\nmodelDisplayName: Weather Agent 2\nmodelDescription: Answers weather questions for US cities.\naction:\n  kind: InvokeExternalAgentTaskAction\n  connectionReference: {WeatherConnectorReference}\n  connectionProperties:\n    mode: Invoker\n\n  operationDetails:\n    kind: ActivityProtocolMetadata\n    operationId: SendActivity\n",
        ["agents/Agent child 1/agent.mcs.yml"] = $"mcs.metadata:\n  componentName: Agent child 1\n  schemaName: {AgentD1Schema}.agent.Agent\nkind: AgentDialog\nbeginDialog:\n  kind: OnToolSelected\n  id: main\n  description: Agent Child 1 Description\n\nsettings: {{}}\ninputType: {{}}\noutputType: {{}}\n",
        ["agents/Agent child 1/actions/promptchild1.mcs.yml"] = PromptAction("prompt child 1", "input_20text", ChildPromptId),
        [$"{ConnectorFolder}/metadata.yml"] = $"{{\n  \"connectorid\": \"{ConnectorId}\",\n  \"name\": \"cre98_5Fweather-20agent-202\",\n  \"displayname\": \"Weather Agent 2\",\n  \"connectorinternalid\": \"shared_cre98-5fweather-20agent-202-5f2c520b622e718fe6\",\n  \"openapidefinition\": \"{ConnectorFolder}/openapidefinition.json\",\n  \"connectionparameters\": \"{ConnectorFolder}/connectionparameters.json\",\n  \"policytemplateinstances\": \"{ConnectorFolder}/policytemplateinstances.json\",\n  \"iconblob\": \"{ConnectorFolder}/iconblob.png\",\n  \"connectortype\": 1\n}}\n",
        [$"{ConnectorFolder}/openapidefinition.json"] = "{\"swagger\":\"2.0\",\"info\":{\"title\":\"Weather Agent 2\",\"version\":\"1.0\"},\"paths\":{\"/api/messages\":{\"post\":{\"operationId\":\"SendActivity\",\"responses\":{\"200\":{\"description\":\"OK\"}}}}}}",
        [$"{ConnectorFolder}/connectionparameters.json"] = "{}",
        [$"{ConnectorFolder}/policytemplateinstances.json"] = "[]",
        [$"{ConnectorFolder}/iconblob.png"] = Icon,
        ["knowledge/files/File 3.txt"] = "Policy 3",
        ["knowledge/files/File3.txt_NbU.mcs.yml"] = KnowledgeSidecar("File 3.txt"),
        ["knowledge/files/abc main 2.txt"] = "Main policy",
        ["knowledge/files/abcmain2.txt_0Sh.mcs.yml"] = KnowledgeSidecar("abc main 2.txt"),
        [$"prompts/summarizedocument1-{SummarizePromptId}/metadata.yml"] = $"aIModelId: {SummarizePromptId}\nname: summarize document 1\ntemplateId: edfdb190-3791-45d8-9a6c-8f90a37c278a\n",
        [$"prompts/summarizedocument1-{SummarizePromptId}/prompt.json"] = PromptJson("summarize document 1", "Text_20input"),
        [$"prompts/promptchild1-{ChildPromptId}/metadata.yml"] = $"aIModelId: {ChildPromptId}\nname: prompt child 1\ntemplateId: edfdb190-3791-45d8-9a6c-8f90a37c278a\n",
        [$"prompts/promptchild1-{ChildPromptId}/prompt.json"] = PromptJson("prompt child 1", "input_20text"),
        ["topics/Greeting.mcs.yml"] = Topic("Greeting", "Hello, how can I help you today?"),
        ["topics/ThankYou.mcs.yml"] = Topic("Thank you", "You're welcome."),
        ["topics/Escalate.mcs.yml"] = Topic("Escalate", "I'll connect you with someone."),
        ["topics/Fallback.mcs.yml"] = $"mcs.metadata:\n  componentName: Fallback\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnUnknownIntent\n  id: main\n  actions:\n    - kind: ConditionGroup\n      id: conditionGroup_LktzXw\n      conditions:\n        - id: conditionItem_tlGIVo\n          condition: =System.FallbackCount < 3\n          actions:\n            - kind: SendActivity\n              id: sendMessage_QZreqo\n              activity: Can you try rephrasing?\n\n      elseActions:\n        - kind: BeginDialog\n          id: 5aXj5M\n          dialog: {AgentD1Schema}.topic.Escalate\n",
        [$"workflows/AgentSDK4Flow1-{FlowId}/metadata.yml"] = $"jsonFileName: workflows/AgentSDK4Flow1-{FlowId}/workflow.json\nworkflowId: {FlowId}\nname: AgentSDK4 Flow1\ntype: 1\ncategory: 5\nmode: 0\nscope: 4\nstateCode: 0\nstatusCode: 1\nrunAs: 1\nmodernFlowType: 1\nprimaryEntity: none\nconnectionReferences:\n- {MsnWeatherReference}\n",
        [$"workflows/AgentSDK4Flow1-{FlowId}/workflow.json"] = AgentWorkspaceHostPushTests.Definition("shared_msnweather", MsnWeatherReference),
    });

    internal static AgentWorkspaceSnapshot Cc1() => Snapshot("S5D2C1", new Dictionary<string, object>
    {
        ["settings.mcs.yml"] = Settings("S5D2C1", Cc1Schema),
        ["agent.mcs.yml"] = "mcs.metadata:\n  componentName: S5D2C1\nkind: GptComponentMetadata\ninstructions: Answer questions about the S5 collection.\n",
        ["icon.png"] = Icon,
        ["references.mcs.yml"] = "componentCollections:\n  - schemaName:\n    directory: ../S5Collection1/\n",
        ["topics/Greeting.mcs.yml"] = Topic("Greeting", "Hello from S5."),
        ["topics/ThankYou.mcs.yml"] = Topic("Thank you", "You're welcome."),
        [$"variables/{Cc1Schema}.GlobalVariableComponent.UserCountry.mcs.yml"] = "mcs.metadata:\n  componentName: UserCountry\n  description: user country variable\nkind: Variable\nname: UserCountry\nscope: Conversation\nisExternalInitializationAllowed: false\nisOutputToExternalCallers: false\ninitializationTimeoutInMilliseconds: 0\n",
        ["../S5Collection1/collection.mcs.yml"] = $"schemaName: {Cc1CollectionSchema}\ndisplayName: S5Collection1\ndescription: S5 component collection 1\nconfiguration:\n  primaryLanguage: 1033\n",
        [$"../S5Collection1/topics/{Cc1Schema}.topic.ConversationStart.mcs.yml"] = Topic("Conversation Start", "Hello, I'm {System.Bot.Name}. How can I help?"),
        [$"../S5Collection1/topics/{Cc1Schema}.topic.Goodbye.mcs.yml"] = Topic("Goodbye", "Goodbye."),
    });

    internal static string Topic(string name, string message) => $"mcs.metadata:\n  componentName: {name}\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    displayName: {name}\n    triggerQueries:\n      - {name} please\n\n  actions:\n    - kind: SendActivity\n      id: sendMessage_{name.Replace(" ", string.Empty, StringComparison.Ordinal)}\n      activity:\n        text:\n          - {message}\n";

    internal static string Variable(string schemaName, string properties) => $"kind: EnvironmentVariableDefinition\nschemaName: {schemaName}\n{properties}";

    private static string Settings(string displayName, string schemaName) => $"displayName: {displayName}\nschemaName: {schemaName}\naccessControlPolicy: GroupMembership\nauthenticationMode: Integrated\nauthenticationTrigger: Always\nconfiguration:\n  settings:\n    GenerativeActionsEnabled: true\n\n  isAgentConnectable: true\n  gPTSettings:\n    defaultSchemaName: {schemaName}.gpt.default\n\n  aISettings:\n    useModelKnowledge: true\n    isFileAnalysisEnabled: true\n    isSemanticSearchEnabled: true\n    contentModeration: Low\n    optInUseLatestModels: false\n\n  recognizer:\n    kind: GenerativeAIRecognizer\n\ntemplate: default-2.1.0\nlanguage: 1033\n";

    private static string PromptAction(string name, string input, Guid promptId) => $"mcs.metadata:\n  componentName: {name}\nkind: TaskDialog\ninputs:\n  - kind: AutomaticTaskInput\n    propertyName: {input}\n    name: {input}\n    shouldPromptUser: true\n\nmodelDisplayName: {name}\nmodelDescription: {name}\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: {promptId}\n\noutputMode: All\n";

    private static string PromptJson(string name, string input) => $"{{\n  \"name\": \"{name}\",\n  \"instruction\": \"Summarize {{{{{input}}}}} in one sentence.\",\n  \"model\": \"gpt-41-mini\",\n  \"inputs\": [\n    {{\n      \"id\": \"{input}\",\n      \"text\": \"{input}\",\n      \"type\": \"text\"\n    }}\n  ],\n  \"output\": {{\n    \"formats\": [\n      \"text\"\n    ]\n  }},\n  \"version\": \"GptDynamicPrompt-2\"\n}}\n";

    private static string KnowledgeSidecar(string fileName) => $"mcs.metadata:\n  componentName: {fileName}\n  description: This knowledge source searches information contained in {fileName}\n";

    private static AgentWorkspaceSnapshot Snapshot(string folder, IReadOnlyDictionary<string, object> files) => new(folder, files.ToDictionary(file => PathOf(folder, file.Key), file => (ReadOnlyMemory<byte>)(file.Value as byte[] ?? Encoding.UTF8.GetBytes((string)file.Value))));

    private static string PathOf(string folder, string relativePath) => relativePath.StartsWith("../", StringComparison.Ordinal) ? relativePath.Substring(3) : $"{folder}/{relativePath}";
}
