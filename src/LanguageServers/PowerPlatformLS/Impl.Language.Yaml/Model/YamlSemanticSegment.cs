namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model
{
    using Microsoft.CopilotStudio.McsCore.Yaml;

    internal class YamlSemanticSegment
    {
        internal YamlSemanticSegment()
        {
            LastScalarValues = new string?[2];
            NextScalarValues = new string?[2];
        }

        internal YamlSemanticSegment(McsYamlNode? secondLastNode, McsYamlNode? lastNode, McsYamlNode? nextNode, McsYamlNode? secondNextNode)
        {
            SecondLastNode = secondLastNode;
            LastNode = lastNode;
            NextNode = nextNode;
            SecondNextNode = secondNextNode;
            LastScalarValues = [secondLastNode?.Scalar, lastNode?.Scalar];
            NextScalarValues = [nextNode?.Scalar, secondNextNode?.Scalar];
        }

        public string?[] LastScalarValues { get; }

        public string?[] NextScalarValues { get; }

        public McsYamlNode? SecondLastNode { get; }

        public McsYamlNode? LastNode { get; }

        public McsYamlNode? NextNode { get; }

        public McsYamlNode? SecondNextNode { get; }
    }
}
