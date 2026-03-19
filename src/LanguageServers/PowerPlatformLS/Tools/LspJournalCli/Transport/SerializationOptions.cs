namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Shared JSON serialization options for the CLI, matching the LSP server conventions.
    /// </summary>
    public static class SerializationOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters = { new NullableJsonElementConverter() },
        };

        /// <summary>
        /// Options for writing journals with indentation for readability.
        /// </summary>
        public static readonly JsonSerializerOptions Indented = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Converters = { new NullableJsonElementConverter() },
        };
    }
}