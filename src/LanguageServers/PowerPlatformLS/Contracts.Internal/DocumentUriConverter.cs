
namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Converter for <see cref="Uri"/>s.
    /// </summary>
    internal class DocumentUriConverter : JsonConverter<Uri>
    {
        public override Uri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.AbsoluteUri);
    }
}