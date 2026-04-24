namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Generators
{
    using Microsoft.Agents.ObjectModel;
    using Schema = Microsoft.Agents.ObjectModel.Schema;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;

    internal interface IBotElementCompletionGenerator
    {
        bool TryGenerateCompletionSnippet(BotElementKind kind, out string? snippet);

        bool TryGenerateCompletionSnippets(Schema.PrimitiveKind kind, DefinitionBase? definition, out ImmutableArray<string> snippets);

        bool TryGetPropertyInfo(BotElementKind kind, string propertyName, [NotNullWhen(true)] out Schema.SchemaPropertyInfo? property);
    }
}
