
namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    internal class FileOperationConverter : JsonConverter<IFileOperation>
    {
        public override IFileOperation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("kind", out JsonElement kindElement))
            {
                string? kind = kindElement.GetString();

                if (kind is null)
                {
                    throw new JsonException("The 'kind' property must be a non-null string.");
                }

                return kind switch
                {
                    RenameFile.KindName => JsonSerializer.Deserialize<RenameFile>(root.GetRawText(), options),
                    TextDocumentEdit.KindName => JsonSerializer.Deserialize<TextDocumentEdit>(root.GetRawText(), options),
                    CreateFile.KindName => JsonSerializer.Deserialize<CreateFile>(root.GetRawText(), options),
                    _ => throw new JsonException($"Unsupported file operation kind: '{kind}'")
                };
            }
            else
            {
                throw new JsonException("The 'kind' property is missing.");
            }
        }

        public override void Write(Utf8JsonWriter writer, IFileOperation value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}