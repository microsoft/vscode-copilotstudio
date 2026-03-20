namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution
{
    using System.Text.Json;

    /// <summary>
    /// Normalizes JSON output for deterministic comparison. Sorts object properties,
    /// normalizes arrays, and removes non-deterministic fields like timestamps and IDs.
    /// </summary>
    public static class OutputNormalizer
    {
        /// <summary>
        /// Fields that are stripped during normalization because they are non-deterministic.
        /// </summary>
        private static readonly HashSet<string> NonDeterministicFields =
        [
            "processId",
            "timestamp",
            "_requestId",
        ];

        /// <summary>
        /// Normalize a JSON element for stable comparison.
        /// </summary>
        public static JsonElement? Normalize(JsonElement? element)
        {
            if (!element.HasValue || element.Value.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            // Preserve JSON null as a real JsonElement — this is a valid response
            // (e.g. shutdown returns {"result": null} per LSP spec).
            if (element.Value.ValueKind == JsonValueKind.Null)
            {
                return element;
            }

            using var doc = JsonDocument.Parse(NormalizeToString(element.Value));
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Normalize a JSON element to a stable, sorted JSON string.
        /// </summary>
        public static string NormalizeToString(JsonElement element)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                WriteNormalized(writer, element);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    // Sort properties by name for deterministic output
                    var properties = new List<JsonProperty>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (!NonDeterministicFields.Contains(prop.Name))
                        {
                            properties.Add(prop);
                        }
                    }

                    properties.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                    foreach (var prop in properties)
                    {
                        writer.WritePropertyName(prop.Name);
                        WriteNormalized(writer, prop.Value);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteNormalized(writer, item);
                    }

                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;

                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var longVal))
                    {
                        writer.WriteNumberValue(longVal);
                    }
                    else
                    {
                        writer.WriteNumberValue(element.GetDouble());
                    }

                    break;

                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;

                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;

                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}