namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Resources
{
    using Microsoft.Agents.ObjectModel;
    using Schema = Microsoft.Agents.ObjectModel.Schema;

    internal class StringResources : IStringResources
    {
        private static readonly IReadOnlyDictionary<string, string> LookupTable;

        static StringResources()
        {
            var resourceName = typeof(StringResources).Namespace + ".Strings.json";
            var content = typeof(StringResources).Assembly.GetManifestResourceStream(resourceName);
            if (content is null)
            {
                throw new InvalidOperationException($"Resource {resourceName} not found.");
            }

            LookupTable = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(content) ?? throw new InvalidOperationException("Content was null");
        }

        public StringResource GetEnumDescription(Schema.PrimitiveKind primitiveKind) => new()
        {
            Title = GetValue($"$$enum_{primitiveKind}_title$$"),
            Description = GetValue($"$$enum_{primitiveKind}_description$$")
        };

        public StringResource GetEnumMemberDescription(Schema.PrimitiveKind primitiveKind, string value) => new()
        {
            Title = GetValue($"$$enum_{primitiveKind}_{value}_title$$"),
            Description = GetValue($"$$enum_{primitiveKind}_{value}_description$$")
        };

        public StringResource GetElementDescription(BotElementKind kind) => new()
        {
            Title = GetValue($"$${kind}_title$$"),
            Description = GetValue($"$${kind}_description$$")
        };

        public StringResource GetPropertyDescription(BotElementKind kind, string propertyName) => new()
        {
            Title = GetValue($"$${kind}_{propertyName}_title$$"),
            Description = GetValue($"$${kind}_{propertyName}_description$$")
        };

        private static string? GetValue(string titleKey)
        {
            return LookupTable.TryGetValue(titleKey, out var title) ? title : null;
        }
    }
}
