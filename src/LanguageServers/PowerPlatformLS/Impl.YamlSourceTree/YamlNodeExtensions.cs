namespace Microsoft.PowerPlatformLS.Impl.YamlSourceTree
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree;
    using YamlDotNet.RepresentationModel;

    internal static class YamlNodeExtensions
    {
        internal static MarkRange GetMarkRange(this YamlNode yamlNode)
        {
            return new MarkRange(yamlNode.Start.ToCommon(), yamlNode.End.ToCommon());
        }

        internal static YNode ToCommon(this YamlNode yamlNode)
        {
            var children = (yamlNode as YamlMappingNode)?.Children;
            var yamlRange = new MarkRange(yamlNode.Start.ToCommon(), (children?.Last().Value ?? yamlNode).End.ToCommon());
            var range = yamlRange.ToLspRange();
            IReadOnlyDictionary<string, YNode> PopulateProperties()
            {
                var properties = new Dictionary<string, YNode>(StringComparer.InvariantCultureIgnoreCase);

                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child.Key is YamlScalarNode keyScalar)
                        {
                            properties[keyScalar.Value ?? string.Empty] = child.Value.ToCommon();
                        }
                    }
                }

                return properties;
            };

            IReadOnlyDictionary<string, string> PopulatePropValue()
            {
                var propValueToPropName = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child.Value is YamlScalarNode valueScalar && valueScalar.Value != null)
                        {
                            propValueToPropName[valueScalar.Value] = child.Key.ToString() ?? string.Empty;
                        }
                    }
                }
                return propValueToPropName;
            }

            return new YNode(range, PopulateProperties, PopulatePropValue);
        }

        internal static Mark ToCommon(this YamlDotNet.Core.Mark mark)
        {
            return new Mark
            {
                Line = (int)mark.Line,
                Column = (int)mark.Column,
            };
        }
    }
}
