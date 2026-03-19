namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree;
    using YamlDotNet.RepresentationModel;

    internal class YamlSemanticModel
    {
        private readonly YamlDocument _document;

        public YamlSemanticModel(string text)
        {
            using var reader = new StringReader(text);
            var scanner = new YamlDotNet.Core.Scanner(reader, skipComments: false);
            var parser = new YamlDotNet.Core.Parser(scanner);
            var yamlStream = new YamlStream();
            yamlStream.Load(parser);
            _document = yamlStream.Documents.First();
        }

        public IEnumerable<YNodeProperty> AllPropertyNodes
        {
            get
            {
                foreach (var node in _document.AllNodes)
                {
                    if (node is YamlMappingNode mappingNode)
                    {
                        foreach (var entry in mappingNode.Children)
                        {
                            if (entry.Key is YamlScalarNode keyNode)
                            {
                                yield return new YNodeProperty(
                                    new MarkRange((int)keyNode.Start.Line, (int)keyNode.Start.Column, (int)keyNode.End.Line, (int)keyNode.End.Column),
                                    keyNode.Value ?? string.Empty,
                                    new MarkRange((int)entry.Value.Start.Line, (int)entry.Value.Start.Column, (int)entry.Value.End.Line, (int)entry.Value.End.Column),
                                    (entry.Value as YamlScalarNode)?.Value);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Temporary helper used while we figure out how to model YAML semantic internally.
        /// Returns the last two and the next two <see cref="YamlNode"/> around the cursor.
        /// Helpful for context-aware completions.
        /// </summary>
        /// <remarks>
        /// TODO : Create an node index to improve performance when accessing nodes by index.
        /// TODO : clean up this hard-coded context to support various context size.
        /// </remarks>
        public YamlSemanticSegment GetSemanticContextAtIndex(int index)
        {
            YamlNode? secondLastNode = null;
            YamlNode? lastNode = null;
            using var nodeIterator = _document.RootNode.AllNodes.GetEnumerator();

            while (nodeIterator.MoveNext())
            {
                var node = nodeIterator.Current;
                if (index < node.Start.Index)
                {
                    YamlNode? nextNode = null;
                    if (nodeIterator.MoveNext())
                    {
                        nextNode = nodeIterator.Current;
                    }

                    return new(secondLastNode, lastNode, node, nextNode);
                }

                secondLastNode = lastNode;
                lastNode = node;
            }

            return new();
        }
    }
}
