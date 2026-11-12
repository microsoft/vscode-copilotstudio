using System.Text;
using Microsoft.CopilotStudio.Sync.Copy;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class WorkflowDefinitionReferencesTests
{
    private const string Definition = """
        {
          "properties": {
            "definition": {
              "actions": {
                "Child": { "type": "workflow", "inputs": { "host": { "workflowReferenceName": "11111111-1111-1111-1111-111111111111" } } },
                "Prompt": { "type": "OpenApiConnection", "inputs": { "host": { "operationId": "AIBuilderPredict_CustomPrompt" }, "parameters": { "recordId": "22222222-2222-2222-2222-222222222222" } } },
                "Scope": { "type": "Scope", "actions": { "ScopedChild": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "33333333-3333-3333-3333-333333333333" } } } } },
                "Condition": { "type": "If", "actions": {}, "else": { "actions": { "ElseChild": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "44444444-4444-4444-4444-444444444444" } } } } } },
                "Switch": {
                  "type": "Switch",
                  "cases": { "Case_1": { "case": "a", "actions": { "CasePrompt": { "type": "OpenApiConnection", "inputs": { "host": { "operationId": "aibuilderpredict_customprompt" }, "parameters": { "recordId": "55555555-5555-5555-5555-555555555555" } } } } } },
                  "default": { "actions": { "DefaultChild": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "66666666-6666-6666-6666-666666666666" } } } } }
                },
                "Compose": { "type": "Compose", "inputs": "11111111-1111-1111-1111-111111111111" },
                "HTTP": { "type": "Http", "inputs": { "host": { "workflowReferenceName": "11111111-1111-1111-1111-111111111111" }, "body": { "recordId": "22222222-2222-2222-2222-222222222222" } } },
                "Get_a_row": { "type": "OpenApiConnection", "inputs": { "host": { "operationId": "GetItem" }, "parameters": { "recordId": "22222222-2222-2222-2222-222222222222" } } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Find_ReportsTheFlowAndPromptEveryActionCallsInEveryScope()
    {
        var references = WorkflowDefinitionReferences.Find(Encoding.UTF8.GetBytes(Definition));

        Assert.Equal(
            new[]
            {
                "aiModel properties/definition/actions/Prompt/inputs/parameters/recordId",
                "aiModel properties/definition/actions/Switch/cases/Case_1/actions/CasePrompt/inputs/parameters/recordId",
                "workflow properties/definition/actions/Child/inputs/host/workflowReferenceName",
                "workflow properties/definition/actions/Condition/else/actions/ElseChild/inputs/host/workflowReferenceName",
                "workflow properties/definition/actions/Scope/actions/ScopedChild/inputs/host/workflowReferenceName",
                "workflow properties/definition/actions/Switch/default/actions/DefaultChild/inputs/host/workflowReferenceName",
            },
            references.Select(reference => $"{reference.Kind} {string.Join("/", reference.Path)}").OrderBy(entry => entry, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{ \"properties\": { \"definition\": { \"actions\": [] } } }")]
    [InlineData("{ \"properties\": { \"definition\": { \"actions\": { \"Child\": { \"type\": \"Workflow\", \"inputs\": { \"host\": { \"workflowReferenceName\": 7 } } } } } } }")]
    public void Find_ADefinitionThatCallsNothing_ReportsNothing(string definition) => Assert.Empty(WorkflowDefinitionReferences.Find(Encoding.UTF8.GetBytes(definition)));

    [Fact]
    public void KindAt_MatchesOnlyTheWholePath()
    {
        var references = new[] { new WorkflowDefinitionReference(new[] { "a", "b" }, CopyIdentityKinds.Workflow) };

        Assert.Equal(CopyIdentityKinds.Workflow, WorkflowDefinitionReferences.KindAt(references, new[] { "a", "b" }));
        Assert.Null(WorkflowDefinitionReferences.KindAt(references, new[] { "a" }));
        Assert.Null(WorkflowDefinitionReferences.KindAt(references, new[] { "a", "b", "c" }));
        Assert.Null(WorkflowDefinitionReferences.KindAt(references, new[] { "x", "b" }));
    }
}
