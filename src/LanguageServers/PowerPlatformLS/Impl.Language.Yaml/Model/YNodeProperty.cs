namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree;

    // Represents the source information for a Yaml property.
    internal record YNodeProperty(MarkRange NameRange, string Name, MarkRange ValueRange, string? ScalarValue)
    {
    }
}
