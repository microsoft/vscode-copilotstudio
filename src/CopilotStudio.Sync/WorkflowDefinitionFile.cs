// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>A connection reference a workflow definition binds to, with the connector it targets.</summary>
public sealed record WorkflowConnectionReference(string LogicalName, string ConnectorInternalId);

/// <summary>Reads and writes the definition file and the connection references it implies.</summary>
public static class WorkflowDefinitionFile
{
    /// <summary>The file a workspace declares its connection references in.</summary>
    public const string ConnectionReferencesFileName = "connectionreferences.mcs.yml";

    /// <summary>The prefix a connector's resource id takes.</summary>
    public const string ConnectorIdPrefix = "/providers/Microsoft.PowerApps/apis/";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Renders a definition the way it is written to disk, leaving unparseable content alone.</summary>
    public static string Format(string? clientData)
    {
        if (string.IsNullOrWhiteSpace(clientData))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(clientData!);

            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return clientData!;
        }
    }

    /// <summary>The connection references a definition declares, with the connector each targets.</summary>
    public static IReadOnlyList<WorkflowConnectionReference> ConnectionReferences(string? clientData) =>
        TryGetConnectionReferences(clientData, out var declared) ? declared : Array.Empty<WorkflowConnectionReference>();

    /// <summary>The connection references a definition declares, reporting whether it could be read at all.</summary>
    /// <param name="clientData">The definition to read.</param>
    /// <param name="declared">The references the definition declares, empty when it could not be read.</param>
    /// <returns><see langword="false"/> when the definition could not be read.</returns>
    public static bool TryGetConnectionReferences(string? clientData, out IReadOnlyList<WorkflowConnectionReference> declared)
    {
        declared = Array.Empty<WorkflowConnectionReference>();

        if (string.IsNullOrWhiteSpace(clientData))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(clientData!);

            return TryGetConnectionReferences(document.RootElement, out declared);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The connection references a parsed definition declares, with the connector each targets.</summary>
    public static IReadOnlyList<WorkflowConnectionReference> ConnectionReferences(JsonElement root) =>
        TryGetConnectionReferences(root, out var declared) ? declared : Array.Empty<WorkflowConnectionReference>();

    /// <summary>The connection references a parsed definition declares, reporting whether its shape is one this understands.</summary>
    /// <param name="root">The parsed definition to read.</param>
    /// <param name="declared">The references the definition declares, empty when its shape is not understood.</param>
    /// <returns><see langword="false"/> when the definition declares its references in a shape this cannot read.</returns>
    public static bool TryGetConnectionReferences(JsonElement root, out IReadOnlyList<WorkflowConnectionReference> declared)
    {
        declared = Array.Empty<WorkflowConnectionReference>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("properties", out var properties))
        {
            return true;
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!properties.TryGetProperty(ConnectionReferencesProperty, out var references))
        {
            return true;
        }

        if (references.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = new List<WorkflowConnectionReference>();

        foreach (var reference in references.EnumerateObject())
        {
            if (reference.Value.ValueKind != JsonValueKind.Object
                || (reference.Value.TryGetProperty("connection", out var connection) && connection.ValueKind != JsonValueKind.Object))
            {
                return false;
            }

            if (Describe(reference) is { } declaration)
            {
                found.Add(declaration);
            }
        }

        declared = found;

        return true;
    }

    /// <summary>The connection reference logical names a definition uses.</summary>
    public static IReadOnlyList<string> ConnectionReferenceNames(string? clientData) => ConnectionReferences(clientData).Select(reference => reference.LogicalName).ToArray();

    /// <summary>Drops the connection bindings that only mean something in the environment a definition came from.</summary>
    /// <param name="clientData">The definition to make portable.</param>
    /// <returns>The definition without its environment-specific bindings, unchanged when there are none to drop.</returns>
    public static string PortableDefinition(string? clientData)
    {
        if (string.IsNullOrWhiteSpace(clientData))
        {
            return string.Empty;
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(clientData!);
        }
        catch (JsonException)
        {
            return clientData!;
        }

        return DropEnvironmentBoundConnections(root) ? root!.ToJsonString(IndentedJson) : clientData!;
    }

    private static bool DropEnvironmentBoundConnections(JsonNode? root)
    {
        var properties = Child(root, "properties");
        var dropped = DropDeclaredConnections(Child(properties, ConnectionReferencesProperty));

        if (Child(Child(properties, "definition"), "triggers") is JsonObject triggers)
        {
            foreach (var trigger in triggers.ToList())
            {
                dropped |= DropDeclaredConnections(DesignerDeclarations(trigger.Value));
            }
        }

        return dropped;
    }

    private static JsonNode? DesignerDeclarations(JsonNode? trigger) =>
        Child(Child(Child(Child(trigger, "metadata"), "associatedData"), "graph"), ConnectionReferencesProperty);

    private static JsonNode? Child(JsonNode? node, string propertyName) =>
        node is JsonObject map && map.TryGetPropertyValue(propertyName, out var value) ? value : null;

    private static bool DropDeclaredConnections(JsonNode? declarations)
    {
        var declared = declarations switch
        {
            JsonObject map => map.Select(entry => entry.Value),
            JsonArray items => items.AsEnumerable(),
            _ => [],
        };

        var dropped = false;

        foreach (var declaration in declared.ToList())
        {
            if (declaration is JsonObject bound
                && bound["connection"] is JsonObject connection
                && connection["connectionReferenceLogicalName"] is not null
                && bound.Remove(EnvironmentBoundConnectionProperty))
            {
                dropped = true;
            }
        }

        return dropped;
    }

    private const string ConnectionReferencesProperty = "connectionReferences";

    private const string EnvironmentBoundConnectionProperty = "connectionName";

    /// <summary>Writes the connection reference declaration every workflow in a workspace implies, deleting it when none remain.</summary>
    /// <param name="workspaceRoot">The workflow workspace root, which must not hold an agent project of its own.</param>
    /// <returns><see langword="true"/> when the file changed.</returns>
    public static bool WriteConnectionReferenceDeclaration(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }

        if (WorkflowWorkspace.HasAgentProject(workspaceRoot))
        {
            throw new ArgumentException("An agent workspace declares its own connection references.", nameof(workspaceRoot));
        }

        var path = Path.Combine(workspaceRoot, ConnectionReferencesFileName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = new List<WorkflowConnectionReference>();

        if (WorkflowWorkspace.HasIncompleteWorkflow(workspaceRoot))
        {
            return false;
        }

        foreach (var folder in WorkflowWorkspace.FindAll(workspaceRoot))
        {
            if (!TryGetConnectionReferences(ReadDefinition(folder.DefinitionPath), out var references))
            {
                return false;
            }

            declared.AddRange(references.Where(reference => seen.Add(reference.LogicalName)));
        }

        if (declared.Count > 0)
        {
            return WriteIfChanged(path, Serialize(declared));
        }

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);

        return true;
    }

    private static string? ReadDefinition(string definitionPath) => File.Exists(definitionPath) ? File.ReadAllText(definitionPath) : null;

    /// <summary>Writes a file only when its content differs from what is already there.</summary>
    public static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return true;
    }

    private static WorkflowConnectionReference? Describe(JsonProperty reference) =>
        reference.Value.ValueKind == JsonValueKind.Object
        && reference.Value.TryGetProperty("connection", out var connection)
        && connection.ValueKind == JsonValueKind.Object
        && ReadString(connection, "connectionReferenceLogicalName") is { Length: > 0 } logicalName
            ? new WorkflowConnectionReference(logicalName, ConnectorInternalId(reference))
            : null;

    private static string ConnectorInternalId(JsonProperty reference) =>
        reference.Value.TryGetProperty("api", out var api) && api.ValueKind == JsonValueKind.Object && ReadString(api, "name") is { Length: > 0 } name ? name : reference.Name;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Serialize(IReadOnlyList<WorkflowConnectionReference> declared)
    {
        using var writer = new StringWriter();

        using (YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false))
        {
            CodeSerializer.SerializeConnectionReferences(writer, declared.Select(reference => new ConnectionReference(
                connectionReferenceLogicalName: reference.LogicalName,
                connectionId: string.Empty,
                connectorId: ConnectorIdPrefix + reference.ConnectorInternalId)).ToArray());
        }

        return writer.ToString();
    }
}
