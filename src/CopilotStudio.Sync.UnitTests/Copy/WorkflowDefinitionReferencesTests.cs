using System.Text;
using Microsoft.CopilotStudio.Sync.Copy;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class WorkflowDefinitionReferencesTests
{
    private const string Definition = """
        {
          "properties": {
            "connectionReferences": {
              "shared_teams": { "api": { "name": "shared_teams" }, "connection": { "connectionReferenceLogicalName": "new_teams" }, "connectionName": "shared-teams-1" }
            },
            "definition": {
              "triggers": {
                "When_a_forecast_changes": {
                  "type": "OpenApiConnectionWebhook",
                  "inputs": { "host": { "apiId": "/providers/Microsoft.PowerApps/apis/shared_weather", "connectionName": "shared_weather", "operationId": "OnForecastChange" } },
                  "metadata": {
                    "associatedData": {
                      "graph": {
                        "nodes": [
                          { "data": { "config": { "apiName": "shared_teams", "connectionName": "shared_teams", "connector": { "apiName": "shared_weather" }, "parameters": { "apiName": "shared_teams" }, "inlineTools": [ { "connectorId": "shared_teams", "connectionReferenceName": "0123" } ] } } }
                        ],
                        "connectionReferences": [ { "api": { "name": "shared_teams" }, "connection": { "connectionReferenceLogicalName": "new_teams" } } ]
                      }
                    }
                  }
                }
              },
              "actions": {
                "Child": { "type": "workflow", "inputs": { "host": { "workflowReferenceName": "11111111-1111-1111-1111-111111111111" } } },
                "Prompt": { "type": "OpenApiConnection", "inputs": { "host": { "operationId": "AIBuilderPredict_CustomPrompt" }, "parameters": { "recordId": "22222222-2222-2222-2222-222222222222" } } },
                "Scope": { "type": "Scope", "actions": { "ScopedChild": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "33333333-3333-3333-3333-333333333333" } } }, "Post": { "type": "OpenApiConnection", "inputs": { "host": { "apiId": "/providers/Microsoft.PowerApps/apis/shared_teams", "connectionName": "shared_teams" } } } } },
                "Condition": { "type": "If", "actions": {}, "else": { "actions": { "ElseChild": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "44444444-4444-4444-4444-444444444444" } } } } } },
                "Switch": {
                  "type": "Switch",
                  "cases": { "Case_1": { "case": "a", "actions": { "CasePrompt": { "type": "OpenApiConnection", "inputs": { "host": { "operationId": "aibuilderpredict_customprompt" }, "parameters": { "recordId": "55555555-5555-5555-5555-555555555555" } } } } } },
                  "default": { "actions": { "DefaultChild": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "66666666-6666-6666-6666-666666666666" } } } } }
                },
                "Compose": { "type": "Compose", "inputs": "11111111-1111-1111-1111-111111111111" },
                "HTTP": { "type": "Http", "inputs": { "host": { "workflowReferenceName": "11111111-1111-1111-1111-111111111111" }, "body": { "recordId": "22222222-2222-2222-2222-222222222222", "connectionReferences": { "shared_teams": { "api": { "name": "shared_teams" }, "connection": { "connectionReferenceLogicalName": "new_teams" } } } } } },
                "Get_a_row": { "type": "OpenApiConnection", "inputs": { "host": { "operationId": "GetItem" }, "parameters": { "recordId": "22222222-2222-2222-2222-222222222222" } } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Find_ReportsOnlyWhatTheFlowDeclaresAndWhatItsTriggersActionsAndDesignerNodesCall()
    {
        Assert.Equal(
            new[]
            {
                "aiModel value properties/definition/actions/Prompt/inputs/parameters/recordId",
                "aiModel value properties/definition/actions/Switch/cases/Case_1/actions/CasePrompt/inputs/parameters/recordId",
                "connectionReference value properties/connectionReferences/shared_teams/connection/connectionReferenceLogicalName",
                "connectionReference value properties/definition/triggers/When_a_forecast_changes/metadata/associatedData/graph/connectionReferences/[]/connection/connectionReferenceLogicalName",
                "connectorInternalId name properties/connectionReferences/shared_teams",
                "connectorInternalId value properties/connectionReferences/shared_teams/api/name",
                "connectorInternalId value properties/definition/actions/Scope/actions/Post/inputs/host/apiId",
                "connectorInternalId value properties/definition/actions/Scope/actions/Post/inputs/host/connectionName",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/inputs/host/apiId",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/inputs/host/connectionName",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/metadata/associatedData/graph/connectionReferences/[]/api/name",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/metadata/associatedData/graph/nodes/[]/data/config/apiName",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/metadata/associatedData/graph/nodes/[]/data/config/connectionName",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/metadata/associatedData/graph/nodes/[]/data/config/connector/apiName",
                "connectorInternalId value properties/definition/triggers/When_a_forecast_changes/metadata/associatedData/graph/nodes/[]/data/config/inlineTools/[]/connectorId",
                "workflow value properties/definition/actions/Child/inputs/host/workflowReferenceName",
                "workflow value properties/definition/actions/Condition/else/actions/ElseChild/inputs/host/workflowReferenceName",
                "workflow value properties/definition/actions/Scope/actions/ScopedChild/inputs/host/workflowReferenceName",
                "workflow value properties/definition/actions/Switch/default/actions/DefaultChild/inputs/host/workflowReferenceName",
            },
            ReferencedTokens(Definition));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{ \"properties\": { \"definition\": { \"actions\": [] } } }")]
    [InlineData("{ \"properties\": { \"definition\": { \"actions\": { \"Child\": { \"type\": \"Workflow\", \"inputs\": { \"host\": { \"workflowReferenceName\": 7 } } } } } } }")]
    [InlineData("{ \"properties\": { \"connectionReferences\": \"shared_teams\" } }")]
    [InlineData("{ \"properties\": { \"definition\": { \"actions\": { \"Post\": { \"inputs\": { \"host\": { \"apiId\": 7, \"connectionName\": null } } } } } } }")]
    [InlineData("{ \"properties\": { \"definition\": { \"triggers\": { \"manual\": { \"metadata\": { \"associatedData\": { \"graph\": { \"nodes\": { \"data\": { \"config\": { \"apiName\": \"shared_teams\" } } } } } } } } } } }")]
    public void Find_ADefinitionThatDeclaresAndCallsNothing_ReportsNothing(string definition) => Assert.Empty(ReferencedTokens(definition));

    [Fact]
    public void KindAt_MatchesOnlyTheWholePathOfTheTokenItRecorded()
    {
        var references = WorkflowDefinitionReferences.Find(Encoding.UTF8.GetBytes("{ \"properties\": { \"connectionReferences\": { \"shared_teams\": { \"api\": { \"name\": \"shared_teams\" } } } } }"));

        Assert.Equal(CopyIdentityKinds.ConnectorInternalId, references.KindAt(Token("properties/connectionReferences/shared_teams/api/name", isPropertyName: false)));
        Assert.Equal(CopyIdentityKinds.ConnectorInternalId, references.KindAt(Token("properties/connectionReferences/shared_teams", isPropertyName: true)));
        Assert.Null(references.KindAt(Token("properties/connectionReferences/shared_teams/api/name", isPropertyName: true)));
        Assert.Null(references.KindAt(Token("properties/connectionReferences/shared_teams", isPropertyName: false)));
        Assert.Null(references.KindAt(Token("properties/connectionReferences/shared_teams/api", isPropertyName: false)));
        Assert.Null(references.KindAt(Token("properties/connectionReferences/shared_teams/api/name/first", isPropertyName: false)));
        Assert.Null(references.KindAt(Token("properties/connectionReferences/shared_other/api/name", isPropertyName: false)));
    }

    private static IEnumerable<string> ReferencedTokens(string definition)
    {
        var content = Encoding.UTF8.GetBytes(definition);
        var references = WorkflowDefinitionReferences.Find(content);
        var referenced = new List<string>();
        CopyTextEdits.VisitJson(content, json =>
        {
            if (references.KindAt(json) is { } kind)
            {
                referenced.Add($"{kind} {(json.IsPropertyName ? "name" : "value")} {string.Join("/", json.Path)}");
            }
        });

        return referenced.OrderBy(entry => entry, StringComparer.Ordinal);
    }

    private static CopyJsonString Token(string path, bool isPropertyName) => new(ownerProperty: null, value: string.Empty, isPropertyName, depth: 0, path.Split('/'));
}
