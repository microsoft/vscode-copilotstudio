namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Custom converter for nullable <see cref="JsonElement"/> that preserves the distinction
    /// between "property absent" and "property is JSON null".
    ///
    /// By default, System.Text.Json deserializes a JSON <c>null</c> token into a
    /// <c>Nullable&lt;JsonElement&gt;</c> as C# <c>null</c> (<c>HasValue = false</c>).
    /// This is lossy: there's no way to tell whether the property was absent from the
    /// JSON object or whether it was explicitly set to <c>null</c>.
    ///
    /// For the journal format this distinction matters:
    /// - Absent <c>"expected"</c> means "no baseline yet" → record the actual response.
    /// - <c>"expected": null</c> means "the expected response IS JSON null"
    ///   (e.g. the LSP <c>shutdown</c> response: <c>{"result": null}</c>).
    ///
    /// This converter reads a JSON <c>null</c> token as a <c>JsonElement</c> with
    /// <c>ValueKind == JsonValueKind.Null</c> (i.e. <c>HasValue = true</c>), so the
    /// round-trip is lossless.
    /// </summary>
    public sealed class NullableJsonElementConverter : JsonConverter<JsonElement?>
    {
        /// <summary>
        /// Critical: when false (default), the serializer returns default(T) for null
        /// tokens without calling Read, which gives us HasValue=false. We need Read to
        /// be called so we can return a JsonElement with ValueKind.Null (HasValue=true).
        /// </summary>
        public override bool HandleNull => true;

        public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // If the token is null, return a JsonElement wrapping null (HasValue=true)
            // instead of C# null (HasValue=false). This preserves the "property exists
            // but is null" semantic.
            if (reader.TokenType == JsonTokenType.Null)
            {
                // Parse a JSON null into a real JsonElement with ValueKind.Null.
                using var doc = JsonDocument.Parse("null");
                return doc.RootElement.Clone();
            }

            // For all other tokens, use the standard deserialization.
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                // C# null → omit property entirely (handled by WhenWritingNull).
                // If we do get called, write JSON null.
                writer.WriteNullValue();
                return;
            }

            value.Value.WriteTo(writer);
        }
    }
}