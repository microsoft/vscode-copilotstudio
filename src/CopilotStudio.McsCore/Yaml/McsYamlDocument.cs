// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>A parsed document exposing the root node plus ordered traversal helpers.</summary>
internal sealed class McsYamlDocument
{
    public McsYamlDocument(McsYamlNode root, bool isEmpty)
    {
        Root = root;
        IsEmpty = isEmpty;
    }

    public McsYamlNode Root { get; }

    public bool IsEmpty { get; }

    public Dictionary<string, object?> ToDictionary()
    {
        if (IsEmpty)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        if (Root.Kind != McsYamlNodeKind.Mapping)
        {
            throw new McsYamlFormatException("Expected a mapping at the root of the document.", Root.Start.Line, Root.Start.Column);
        }

        return (Dictionary<string, object?>)Root.ToValue()!;
    }

    public IEnumerable<McsYamlProperty> AllProperties() => DescendProperties(Root);

    /// <summary>Enumerates every node, including mappings and sequences, in document order.</summary>
    public IEnumerable<McsYamlNode> NodesInDocumentOrder() => Descend(Root);

    private static IEnumerable<McsYamlNode> Descend(McsYamlNode node)
    {
        yield return node;

        if (node.IsAlias)
        {
            yield break;
        }

        if (node.Properties != null)
        {
            foreach (var property in node.Properties)
            {
                yield return property.Key;

                foreach (var nested in Descend(property.Value))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (node.Items != null)
        {
            foreach (var item in node.Items)
            {
                foreach (var nested in Descend(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<McsYamlProperty> DescendProperties(McsYamlNode node)
    {
        if (node.IsAlias)
        {
            yield break;
        }

        if (node.Properties != null)
        {
            foreach (var property in node.Properties)
            {
                yield return property;

                foreach (var nested in DescendProperties(property.Value))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (node.Items != null)
        {
            foreach (var item in node.Items)
            {
                foreach (var nested in DescendProperties(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
