namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Generators
{
    using Microsoft.Agents.ObjectModel;
    using Schema = Microsoft.Agents.ObjectModel.Schema;
    using System;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;

    internal class BotElementCompletionGenerator : IBotElementCompletionGenerator
    {
        private static readonly ImmutableArray<string> BooleanOptions = ["true", "false"];
        private static readonly IReadOnlyDictionary<(BotElementKind kind, string propertyName), Schema.SchemaPropertyInfo> AllProperties = Schema.SchemaData.Properties;
        private static readonly IReadOnlyDictionary<Schema.PrimitiveKind, ImmutableArray<string>> PrimitiveSnippets = BuildPrimitiveSnippets();

        public bool TryGenerateCompletionSnippet(BotElementKind kind, out string? snippet)
        {
            snippet = null;
            return false;
        }

        public bool TryGetPropertyInfo(BotElementKind kind, string propertyName, [NotNullWhen(true)] out Schema.SchemaPropertyInfo? property)
        {
            return AllProperties.TryGetValue((kind, propertyName), out property);
        }

        public bool TryGenerateCompletionSnippets(Schema.PrimitiveKind kind, out ImmutableArray<string> snippets)
        {
            return PrimitiveSnippets.TryGetValue(kind, out snippets);
        }

        private static IReadOnlyDictionary<Schema.PrimitiveKind, ImmutableArray<string>> BuildPrimitiveSnippets()
        {
            var properties = new Dictionary<Schema.PrimitiveKind, ImmutableArray<string>>();
            ImmutableArray<string> variableCompletions = ["Topic.", "Global.", "System."];
            properties[Schema.PrimitiveKind.PropertyPath] = variableCompletions;
            properties[Schema.PrimitiveKind.InitializablePropertyPath] = variableCompletions.AddRange(variableCompletions.Select(s => "init:" + s));
            foreach (var kvp in Schema.SchemaData.EnumValues)
            {
                properties[kvp.Key] = kvp.Value;
            }

            AddBuiltInPrimitives(properties);
            return properties;
        }

        private static void AddBuiltInPrimitives(Dictionary<Schema.PrimitiveKind, ImmutableArray<string>> properties)
        {
            properties[Schema.PrimitiveKind.@bool] = BooleanOptions;
        }
    }
}
