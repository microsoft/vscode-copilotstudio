// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace Microsoft.CopilotStudio.Sync.Copy;

internal sealed class WorkflowDefinitionReferences
{
    private const string ChildFlowActionType = "Workflow";

    private const string AIBuilderOperationPrefix = "aibuilderpredict";

    private static readonly string[] PropertiesPath = { "properties" };

    private static readonly string[] DefinitionPath = { "definition" };

    private static readonly string[] TriggersPath = { "triggers" };

    private static readonly string[] ActionsPath = { "actions" };

    private static readonly string[] CasesPath = { "cases" };

    private static readonly string[][] BranchPaths = { new[] { "else" }, new[] { "default" } };

    private static readonly string[] DeclarationsPath = { "connectionReferences" };

    private static readonly string[] GraphPath = { "metadata", "associatedData", "graph" };

    private static readonly string[] NodesPath = { "nodes" };

    private static readonly string[] InlineToolsPath = { "data", "config", "inlineTools" };

    private static readonly string[] ActionTypePath = { "type" };

    private static readonly string[] OperationPath = { "inputs", "host", "operationId" };

    private static readonly string[] ChildFlowReferencePath = { "inputs", "host", "workflowReferenceName" };

    private static readonly string[] AIModelReferencePath = { "inputs", "parameters", "recordId" };

    private static readonly string[] DeclaredConnectorPath = { "api", "name" };

    private static readonly string[] DeclaredReferencePath = { "connection", WorkspaceCopyTransform.ConnectionReferenceLogicalNameKey };

    private static readonly string[] InlineToolConnectorPath = { "connectorId" };

    private static readonly string[][] HostConnectorPaths = { new[] { "inputs", "host", "apiId" }, new[] { "inputs", "host", "connectionName" } };

    private static readonly string[][] NodeConnectorPaths = { new[] { "data", "config", "apiName" }, new[] { "data", "config", "connectionName" }, new[] { "data", "config", "connector", "apiName" } };

    private readonly Dictionary<string, WorkflowDefinitionReferences> _children = new(StringComparer.Ordinal);

    private string? _valueKind;

    private string? _propertyNameKind;

    internal static WorkflowDefinitionReferences Find(byte[] content)
    {
        using var document = JsonDocument.Parse(CopyTextEdits.WithoutPreamble(content), WorkflowDefinitionFile.StrictJson);

        return Find(document.RootElement);
    }

    internal static WorkflowDefinitionReferences Find(JsonElement root)
    {
        var references = new WorkflowDefinitionReferences();
        var path = new List<string>();

        Visit(root, PropertiesPath, path, properties =>
        {
            references.VisitDeclarations(properties, path);
            Visit(properties, DefinitionPath, path, definition =>
            {
                VisitEach(definition, TriggersPath, path, trigger => references.VisitTrigger(trigger, path));
                references.VisitScope(definition, path);
            });
        });

        return references;
    }

    internal string? KindAt(CopyJsonString json)
    {
        var node = this;

        for (var index = 0; index < json.Path.Count; index++)
        {
            if (!node._children.TryGetValue(json.Path[index], out node))
            {
                return null;
            }
        }

        return json.IsPropertyName ? node._propertyNameKind : node._valueKind;
    }

    private void VisitTrigger(JsonElement trigger, List<string> path)
    {
        AddStrings(trigger, HostConnectorPaths, CopyIdentityKinds.ConnectorInternalId, path);
        Visit(trigger, GraphPath, path, graph =>
        {
            VisitDeclarations(graph, path);
            VisitItems(graph, NodesPath, path, node =>
            {
                AddStrings(node, NodeConnectorPaths, CopyIdentityKinds.ConnectorInternalId, path);
                VisitItems(node, InlineToolsPath, path, tool => AddString(tool, InlineToolConnectorPath, CopyIdentityKinds.ConnectorInternalId, path));
            });
        });
    }

    private void VisitScope(JsonElement scope, List<string> path) => VisitEach(scope, ActionsPath, path, action => VisitAction(action, path));

    private void VisitAction(JsonElement action, List<string> path)
    {
        AddStrings(action, HostConnectorPaths, CopyIdentityKinds.ConnectorInternalId, path);

        if (string.Equals(StringAt(action, ActionTypePath), ChildFlowActionType, StringComparison.OrdinalIgnoreCase))
        {
            AddString(action, ChildFlowReferencePath, CopyIdentityKinds.Workflow, path);
        }

        if (StringAt(action, OperationPath) is { } operation && operation.StartsWith(AIBuilderOperationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddString(action, AIModelReferencePath, CopyIdentityKinds.AIModel, path);
        }

        VisitScope(action, path);

        foreach (var branch in BranchPaths)
        {
            Visit(action, branch, path, scope => VisitScope(scope, path));
        }

        VisitEach(action, CasesPath, path, branch => VisitScope(branch, path));
    }

    private void VisitDeclarations(JsonElement owner, List<string> path)
    {
        VisitEach(owner, DeclarationsPath, path, declaration =>
        {
            Add(path, CopyIdentityKinds.ConnectorInternalId, isPropertyName: true);
            VisitDeclaration(declaration, path);
        });

        VisitItems(owner, DeclarationsPath, path, declaration => VisitDeclaration(declaration, path));
    }

    private void VisitDeclaration(JsonElement declaration, List<string> path)
    {
        AddString(declaration, DeclaredConnectorPath, CopyIdentityKinds.ConnectorInternalId, path);
        AddString(declaration, DeclaredReferencePath, CopyIdentityKinds.ConnectionReference, path);
    }

    private void AddStrings(JsonElement element, string[][] relativePaths, string kind, List<string> path)
    {
        foreach (var relativePath in relativePaths)
        {
            AddString(element, relativePath, kind, path);
        }
    }

    private void AddString(JsonElement element, string[] relativePath, string kind, List<string> path)
    {
        if (StringAt(element, relativePath) is not null)
        {
            Add(path.Concat(relativePath), kind, isPropertyName: false);
        }
    }

    private void Add(IEnumerable<string> path, string kind, bool isPropertyName)
    {
        var node = this;

        foreach (var segment in path)
        {
            if (!node._children.TryGetValue(segment, out var child))
            {
                child = new WorkflowDefinitionReferences();
                node._children[segment] = child;
            }

            node = child;
        }

        if (isPropertyName)
        {
            node._propertyNameKind = kind;
        }
        else
        {
            node._valueKind = kind;
        }
    }

    private static void Visit(JsonElement element, string[] relativePath, List<string> path, Action<JsonElement> visit)
    {
        if (ElementAt(element, relativePath) is { } found)
        {
            path.AddRange(relativePath);
            visit(found);
            path.RemoveRange(path.Count - relativePath.Length, relativePath.Length);
        }
    }

    private static void VisitEach(JsonElement element, string[] relativePath, List<string> path, Action<JsonElement> visit)
    {
        Visit(element, relativePath, path, map =>
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var entry in map.EnumerateObject())
            {
                path.Add(entry.Name);
                visit(entry.Value);
                path.RemoveAt(path.Count - 1);
            }
        });
    }

    private static void VisitItems(JsonElement element, string[] relativePath, List<string> path, Action<JsonElement> visit)
    {
        Visit(element, relativePath, path, list =>
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            path.Add(CopyTextEdits.ArrayItemSegment);

            foreach (var item in list.EnumerateArray())
            {
                visit(item);
            }

            path.RemoveAt(path.Count - 1);
        });
    }

    private static string? StringAt(JsonElement element, string[] path) => ElementAt(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static JsonElement? ElementAt(JsonElement element, string[] path)
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
