namespace Microsoft.PowerPlatformLS.Imple.YamlSourceTree
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Xml.Linq;
    using System.Xml;
    using YamlDotNet.RepresentationModel;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree;
    using Microsoft.PowerPlatformLS.Impl.YamlSourceTree;

    /// <summary>
    /// Wrapper on <see cref="YamlDocument"/> exposing methods to navigate properties in the document.
    /// </summary>
    public class YSourceTree
    {
        /// <summary>
        /// The root node of the document.
        /// </summary>
        private readonly YamlDocument _document;

        private readonly NodeIndex? _nodeIndex;

        /// <summary>
        /// Initializes a new instance of the <see cref="YSourceTree"/> class.
        /// </summary>
        /// <param name="text">Yaml text to parse.</param>
        /// <param name="indexKey">Optional key to index nodes by.</param>
        /// <exception cref="InvalidSyntaxException">Thrown when the YAML syntax is invalid.</exception>
        public YSourceTree(string text, string? indexKey = null)
        {
            try
            {
                using var reader = new StringReader(text);
                var scanner = new YamlDotNet.Core.Scanner(reader, skipComments: false);
                var parser = new YamlDotNet.Core.Parser(scanner);
                var yamlStream = new YamlStream();
                yamlStream.Load(parser);
                _document = yamlStream.Documents.First();

                // There must be a way to build the index on the fly using TypeConverter
                // but it's currently out of scope as this code is for temporary POC.
                _nodeIndex = indexKey == null ? null : new NodeIndex(indexKey, _document);
            }
            catch (YamlDotNet.Core.YamlException exc)
            {
                throw new InvalidSyntaxException(exc, new MarkRange(exc.Start.ToCommon(), exc.End.ToCommon()));
            }
            catch (Exception)
            {
                // TODO wrap unhandled exception to avoid exposing YDN types.
                throw;
            }
        }

        /// <summary>
        /// Gets nodes by indexed value.
        /// </summary>
        /// <param name="id">The id to search for.</param>
        /// <returns>Enumerable of <see cref="YNode"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the source was not indexed.</exception>
        public IEnumerable<YNode> GetByIndexedValue(string id)
        {
            return _nodeIndex?.Get(id) ?? throw new InvalidOperationException($"Source was not indexed. You must pass index key {nameof(YSourceTree)} constructor.");
        }

        /// <summary>
        /// Expands the children nodes of a given parent node.
        /// </summary>
        /// <param name="parent">The parent node.</param>
        /// <returns>Enumerable of <see cref="YamlNode"/>.</returns>
        private IEnumerable<YamlNode> ExpandChildrenNodes(YamlNode parent)
        {
            if (parent is IEnumerable<KeyValuePair<YamlNode, YamlNode>> parentMap)
            {
                return parentMap.Select(kv => kv.Value);
            }
            else if (parent is IEnumerable<YamlNode> seq)
            {
                return seq;
            }
            else
            {
                return Enumerable.Empty<YamlNode>();
            }
        }

        /// <summary>
        /// Finds a child node with a specific property value.
        /// </summary>
        /// <param name="parent">The parent node.</param>
        /// <param name="propName">The property name to search for.</param>
        /// <param name="propValue">The property value to match.</param>
        /// <param name="stopWalkingOnProp">Whether to stop walking the tree when the property is found.</param>
        /// <returns>The matching <see cref="YamlMappingNode"/> or null if not found.</returns>
        protected YamlMappingNode? FindChildWithPropertyValue(YamlMappingNode parent, string propName, string propValue, bool stopWalkingOnProp)
        {
            var candidates = new Queue<YamlNode>();
            foreach (var child in ExpandChildrenNodes(parent))
            {
                candidates.Enqueue(child);
            }

            bool isWalkable = true;

            while (candidates.Count > 0)
            {
                var cur = candidates.Dequeue();
                if (cur is YamlMappingNode mapNode)
                {
                    if (mapNode.Children.TryGetValue(propName, out var propNode))
                    {
                        isWalkable = !stopWalkingOnProp;
                        if (propNode is YamlScalarNode valueNode && propValue.Equals(valueNode.Value))
                        {
                            return mapNode;
                        }
                    }
                }

                if (isWalkable)
                {
                    foreach (var child in ExpandChildrenNodes(cur))
                    {
                        candidates.Enqueue(child);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a node by property value.
        /// </summary>
        /// <param name="propValue">The property value to search for.</param>
        /// <param name="propName">The property name to search for.</param>
        /// <returns>The matching <see cref="YNode"/>.</returns>
        public YNode GetNodeByPropValue(string propValue, string propName)
        {
            if (propName.Equals(_nodeIndex?.IndexKey, StringComparison.InvariantCultureIgnoreCase))
            {
                return GetByIndexedValue(propValue).First();
            }

            return GetNodeByPropertyPath(new[] { propValue }, propName, isPathComplete: false);
        }

        /// <summary>
        /// Get a node that matches property criteria:
        /// - The node has a property with the specified name.
        /// - The property value matches the specified value.
        /// - All the parents of the node with the property specified have the property value specified in the path.
        /// </summary>
        /// <example>
        /// Given propPath = ["a", "b", "c"], propName = "id", isPathComplete = true
        /// The method will return the node with id "c" only if the node with id "a" has a child or grand-child with id "b".
        /// id: a
        /// children:
        ///   - id: b
        ///     children:
        ///       - id: c
        /// </example>
        /// <param name="propPath">The path of property values to lookup the node.</param>
        /// <param name="propName">The name of the property to match.</param>
        /// <param name="isPathComplete">
        /// Whether the id path is complete.
        /// If it is complete, no Id can exist in the source between two nodes with ids specified.
        /// Having a complete id path helps narrow down the search greatly (log(n)) vs linear).
        /// </param>
        /// <returns>
        /// The node found by looking up the id path.
        /// If an id can't be found in source, the last node in the path is returned, defaulting to the root.
        /// </returns>
        public YNode GetNodeByPropertyPath(string[] propPath, string propName, bool isPathComplete = true)
        {
            if (_document.RootNode is not YamlMappingNode cur)
            {
                return _document.RootNode.ToCommon();
            }

            for (int idx = 0; idx < propPath.Length; idx++)
            {
                var curId = propPath[idx];

                var nextCur = FindChildWithPropertyValue(cur, propName, curId, isPathComplete);
                if (nextCur == null)
                {
                    break;
                }

                cur = nextCur;
            }

            return cur.ToCommon();
        }

        /// <summary>
        /// Represents an index of nodes by a specific key.
        /// </summary>
        private class NodeIndex
        {
            private readonly IImmutableDictionary<string, YNode[]> _index;
            public string IndexKey { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="NodeIndex"/> class.
            /// </summary>
            /// <param name="indexKey">The key to index nodes by.</param>
            /// <param name="document">The YAML document to index.</param>
            public NodeIndex(string indexKey, YamlDocument document)
            {
                IndexKey = indexKey;
                _index = BuildNodeIndex(document, indexKey);
            }

            /// <summary>
            /// Gets nodes by the specified id.
            /// </summary>
            /// <param name="id">The id to search for.</param>
            /// <returns>Enumerable of <see cref="YNode"/>.</returns>
            public IEnumerable<YNode> Get(string id)
            {
                if (_index.TryGetValue(id, out var nodes))
                {
                    return nodes;
                }
                return Enumerable.Empty<YNode>();
            }

            /// <summary>
            /// Builds an index of nodes by the specified key.
            /// </summary>
            /// <param name="document">The YAML document to index.</param>
            /// <param name="indexKey">The key to index nodes by.</param>
            /// <returns>An immutable dictionary of nodes indexed by the key.</returns>
            private static IImmutableDictionary<string, YNode[]> BuildNodeIndex(YamlDocument document, string indexKey)
            {
                var indexBuilder = ImmutableDictionary.CreateBuilder<string, YNode[]>();
                foreach (var node in document.AllNodes)
                {
                    if (node is YamlMappingNode mapNode)
                    {
                        if (mapNode.Children.TryGetValue(indexKey, out var idNode))
                        {
                            var key = (idNode as YamlScalarNode)?.Value ?? string.Empty;
                            YNode[] value = [node.ToCommon()];
                            if (!indexBuilder.TryAdd(key, value))
                            {
                                // This is the case where we have multiple nodes with the same Id.
                                // It's okay to pay the cost of concatenating the arrays here since this is an error case.
                                indexBuilder[key] = indexBuilder[key].Concat(value).ToArray();
                            }
                        }
                    }
                }

                return indexBuilder.ToImmutable();
            }
        }
    }
}
