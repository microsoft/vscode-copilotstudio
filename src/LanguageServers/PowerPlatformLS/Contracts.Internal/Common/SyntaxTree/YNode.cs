namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Diagnostics.CodeAnalysis;

    public class YNode
    {
        private readonly Func<IReadOnlyDictionary<string, string>> _propValueToPropNameGenerator;
        private readonly Func<IReadOnlyDictionary<string, YNode>> _propertiesGenerator;
        private IReadOnlyDictionary<string, YNode>? _properties;
        private IReadOnlyDictionary<string, string>? _propValueToPropName;

        public YNode(Range lspRange, Func<IReadOnlyDictionary<string, YNode>>? propertiesGenerator = null, Func<IReadOnlyDictionary<string, string>>? propValueToPropNameGenerator = null)
        {
            _propValueToPropNameGenerator = propValueToPropNameGenerator ?? (() => new Dictionary<string, string>());
            _propertiesGenerator = propertiesGenerator ?? (() => new Dictionary<string, YNode>());
            Range = lspRange;
        }

        public Range Range { get; }

        public bool TryGetPropertyNode(string propertyName, [MaybeNullWhen(false)] out YNode propNode)
        {
            return Properties.TryGetValue(propertyName, out propNode);
        }

        public bool TryGetPropertyNodeByValue(string propValue, [MaybeNullWhen(false)] out YNode propNode)
        {
            if (PropValueToPropName.TryGetValue(propValue, out var propName))
            {
                return TryGetPropertyNode(propName, out propNode);
            }

            propNode = null;
            return false;
        }

        private IReadOnlyDictionary<string, YNode> Properties => _properties ??= _propertiesGenerator();
        private IReadOnlyDictionary<string, string> PropValueToPropName => _propValueToPropName ??= _propValueToPropNameGenerator();
    }
}