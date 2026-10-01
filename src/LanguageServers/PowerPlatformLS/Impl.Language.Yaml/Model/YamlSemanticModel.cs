namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model
{
    using Microsoft.CopilotStudio.McsCore.Yaml;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree;

    internal class YamlSemanticModel
    {
        private readonly McsYamlDocument _document;

        public YamlSemanticModel(string text)
        {
            _document = McsYamlReader.ParseDocument(text);
        }

        public IEnumerable<YNodeProperty> AllPropertyNodes
        {
            get
            {
                foreach (var property in _document.AllProperties())
                {
                    yield return new YNodeProperty(
                        new MarkRange(property.NameStart.Line, property.NameStart.Column, property.NameEnd.Line, property.NameEnd.Column),
                        property.Name,
                        new MarkRange(property.Value.Start.Line, property.Value.Start.Column, property.Value.End.Line, property.Value.End.Column),
                        property.Value.Scalar);
                }
            }
        }

        public YamlSemanticSegment GetSemanticContextAtIndex(int index)
        {
            McsYamlNode? secondLastNode = null;
            McsYamlNode? lastNode = null;
            using var nodeIterator = _document.NodesInDocumentOrder().GetEnumerator();

            while (nodeIterator.MoveNext())
            {
                var node = nodeIterator.Current;
                if (index < node.Start.Index)
                {
                    return new(secondLastNode, lastNode, node, nodeIterator.MoveNext() ? nodeIterator.Current : null);
                }

                secondLastNode = lastNode;
                lastNode = node;
            }

            return new();
        }
    }
}
