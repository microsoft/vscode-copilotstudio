namespace Microsoft.PowerPlatformLS.Impl.Core.Serialization
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using System.Text.Json;

    /// <summary>
    /// Serializer.
    ///
    /// TODO: Whenever this needs to be shared again, avoid public static helper and use DI instead.
    /// Whenever this needs to be shared again, let's avoid public static helper and use DI instead.
    /// We should also add functionalities like <see cref="JsonSerializer.SerializeToElement{TValue}(TValue, System.Text.Json.JsonSerializerOptions?)"/>.
    /// </summary>
    internal static class LspJsonSerializationHelper
    {
        public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            options ??= Constants.DefaultSerializationOptions;
            return JsonSerializer.Deserialize<T>(json, options) ?? default;
        }

        public static string Serialize<T>(T obj, JsonSerializerOptions? options = null)
        {
            try
            {
                options ??= Constants.DefaultSerializationOptions;
                return JsonSerializer.Serialize(obj, options);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}