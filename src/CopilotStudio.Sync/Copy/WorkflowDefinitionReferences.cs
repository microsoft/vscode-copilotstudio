// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace Microsoft.CopilotStudio.Sync.Copy;

internal sealed record WorkflowDefinitionReference(IReadOnlyList<string> Path, string Kind);

internal static class WorkflowDefinitionReferences
{
    private const string ChildFlowActionType = "Workflow";

    private const string AIBuilderOperationPrefix = "aibuilderpredict";

    private const string ActionsProperty = "actions";

    private const string CasesProperty = "cases";

    private static readonly string[] DefinitionPath = { "properties", "definition" };

    private static readonly string[] ActionTypePath = { "type" };

    private static readonly string[] OperationPath = { "inputs", "host", "operationId" };

    private static readonly string[] ChildFlowReferencePath = { "inputs", "host", "workflowReferenceName" };

    private static readonly string[] AIModelReferencePath = { "inputs", "parameters", "recordId" };

    private static readonly string[] BranchProperties = { "else", "default" };

    internal static IReadOnlyList<WorkflowDefinitionReference> Find(byte[] content)
    {
        using var document = JsonDocument.Parse(CopyTextEdits.WithoutPreamble(content), WorkflowDefinitionFile.StrictJson);

        return Find(document.RootElement);
    }

    internal static IReadOnlyList<WorkflowDefinitionReference> Find(JsonElement root)
    {
        var references = new List<WorkflowDefinitionReference>();

        if (ElementAt(root, DefinitionPath) is { } definition)
        {
            VisitScope(definition, new List<string>(DefinitionPath), references);
        }

        return references;
    }

    internal static string? KindAt(IReadOnlyList<WorkflowDefinitionReference> references, IReadOnlyList<string> path) => references.FirstOrDefault(reference => reference.Path.Count == path.Count && reference.Path.SequenceEqual(path, StringComparer.Ordinal))?.Kind;

    private static void VisitScope(JsonElement scope, List<string> path, List<WorkflowDefinitionReference> references)
    {
        if (ElementAt(scope, ActionsProperty) is { ValueKind: JsonValueKind.Object } actions)
        {
            Within(path, ActionsProperty, () =>
            {
                foreach (var action in actions.EnumerateObject())
                {
                    Within(path, action.Name, () => VisitAction(action.Value, path, references));
                }
            });
        }
    }

    private static void VisitAction(JsonElement action, List<string> path, List<WorkflowDefinitionReference> references)
    {
        if (string.Equals(StringAt(action, ActionTypePath), ChildFlowActionType, StringComparison.OrdinalIgnoreCase))
        {
            AddReference(action, path, ChildFlowReferencePath, CopyIdentityKinds.Workflow, references);
        }

        if (StringAt(action, OperationPath) is { } operation && operation.StartsWith(AIBuilderOperationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddReference(action, path, AIModelReferencePath, CopyIdentityKinds.AIModel, references);
        }

        VisitScope(action, path, references);

        foreach (var branch in BranchProperties)
        {
            if (ElementAt(action, branch) is { ValueKind: JsonValueKind.Object } scope)
            {
                Within(path, branch, () => VisitScope(scope, path, references));
            }
        }

        if (ElementAt(action, CasesProperty) is { ValueKind: JsonValueKind.Object } cases)
        {
            Within(path, CasesProperty, () =>
            {
                foreach (var branch in cases.EnumerateObject())
                {
                    Within(path, branch.Name, () => VisitScope(branch.Value, path, references));
                }
            });
        }
    }

    private static void AddReference(JsonElement action, List<string> path, string[] referencePath, string kind, List<WorkflowDefinitionReference> references)
    {
        if (StringAt(action, referencePath) is not null)
        {
            references.Add(new WorkflowDefinitionReference(path.Concat(referencePath).ToArray(), kind));
        }
    }

    private static void Within(List<string> path, string segment, Action visit)
    {
        path.Add(segment);
        visit();
        path.RemoveAt(path.Count - 1);
    }

    private static string? StringAt(JsonElement element, string[] path) => ElementAt(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static JsonElement? ElementAt(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element;
    }
}
