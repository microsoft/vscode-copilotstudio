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
        /// Journal baselines are committed as LF, so newlines are pinned to "\n"
        /// to keep regenerated baselines byte-stable across operating systems
        /// (System.Text.Json otherwise defaults to Environment.NewLine).
        /// </summary>
        public static readonly JsonSerializerOptions Indented = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            NewLine = "\n",
            Converters = { new NullableJsonElementConverter() },
        };
    }
}