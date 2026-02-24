
namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model
{
    using YamlDotNet.RepresentationModel;

    /// <summary>
    /// Temporary - we want to avoid leaking YamlDotNet types to the rest of the codebase.
    /// </summary>
    internal class YamlSemanticSegment
    {
        internal YamlSemanticSegment()
        {
            LastScalarValues = new string[2];
            NextScalarValues = new string[2];
        }

        internal YamlSemanticSegment(YamlNode? secondLastNode, YamlNode? lastNode, YamlNode? nextNode, YamlNode? secondNextNode)
        {
            SecondLastNode = secondLastNode;
            LastNode = lastNode;
            NextNode = nextNode;
            SecondNextNode = secondNextNode;
            LastScalarValues = [(SecondLastNode as YamlScalarNode)?.Value, (LastNode as YamlScalarNode)?.Value];
            NextScalarValues = [(NextNode as YamlScalarNode)?.Value, (SecondNextNode as YamlScalarNode)?.Value];
        }

        public string?[] LastScalarValues { get; }
        public string?[] NextScalarValues { get; }

        public YamlNode? SecondLastNode { get; }
        public YamlNode? LastNode { get; }
        public YamlNode? NextNode { get; }
        public YamlNode? SecondNextNode { get; }
    }
}