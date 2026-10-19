// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Maps metadata sidecar documents to and from plain objects using the historical camel-cased key names.</summary>
internal static class McsYamlObjectMapper
{
    private static readonly Dictionary<Type, TypeBindings> BindingCache = new();

    public static string SerializeName(string propertyName) => propertyName.Length == 0 ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);

    public static Dictionary<string, object?> ToDictionary(object value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var binding in GetBindings(value.GetType()).Properties)
        {
            result[binding.Key] = ToDocumentValue(binding.Property.GetValue(value));
        }

        return result;
    }

    public static T? Deserialize<T>(string yaml) where T : class, new() => Deserialize<T>(yaml, rejectUnknownProperties: false);

    /// <summary>Reads a document and fails when it contains a property the target type does not declare.</summary>
    public static T? DeserializeStrict<T>(string yaml) where T : class, new() => Deserialize<T>(yaml, rejectUnknownProperties: true);

    private static T? Deserialize<T>(string yaml, bool rejectUnknownProperties) where T : class, new()
    {
        var document = McsYamlReader.ParseDocument(yaml);
        return document.IsEmpty ? null : (T?)FromDocumentValue(typeof(T), document.Root.ToValue(preserveStringTags: true), typeof(T).Name, rejectUnknownProperties);
    }

    public static string Serialize(object value) => McsYamlWriter.Write(ToDictionary(value));

    private static object FromDictionary(Type type, IDictionary<string, object?> source, bool rejectUnknownProperties)
    {
        var instance = Activator.CreateInstance(type)!;
        var bindings = GetBindings(type);

        foreach (var binding in bindings.Properties)
        {
            if (source.TryGetValue(binding.Key, out var raw))
            {
                binding.Property.SetValue(instance, FromDocumentValue(binding.Property.PropertyType, raw, binding.Key, rejectUnknownProperties));
            }
        }

        if (rejectUnknownProperties)
        {
            foreach (var key in source.Keys)
            {
                if (!bindings.Keys.Contains(key))
                {
                    throw new McsYamlFormatException($"Property '{key}' is not recognized on '{type.Name}'.");
                }
            }
        }

        return instance;
    }

    private static object? ToDocumentValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case Guid identifier:
                return identifier.ToString();
            case IEnumerable sequence when value is not string:
                {
                    var items = new List<object?>();
                    foreach (var item in sequence)
                    {
                        items.Add(ToDocumentValue(item));
                    }

                    return items;
                }
            default:
                return value.GetType().IsPrimitive || value is decimal
                    ? Convert.ToString(value, CultureInfo.InvariantCulture)
                    : ToDictionary(value);
        }
    }

    private static object? FromDocumentValue(Type target, object? raw, string key, bool rejectUnknownProperties)
    {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (raw is IDictionary<string, object?> nested)
        {
            if (underlying == typeof(string) || underlying == typeof(Guid) || underlying.IsPrimitive || typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                throw new McsYamlFormatException($"Property '{key}' expects {DescribeType(underlying)} but the document contains a mapping.");
            }

            return FromDictionary(underlying, nested, rejectUnknownProperties);
        }

        if (raw is IList<object?> items)
        {
            if (underlying == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                throw new McsYamlFormatException($"Property '{key}' expects {DescribeType(underlying)} but the document contains a sequence.");
            }

            var elementType = underlying.IsGenericType ? underlying.GetGenericArguments()[0] : typeof(string);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

            foreach (var item in items)
            {
                list.Add(FromDocumentValue(elementType, item, key, rejectUnknownProperties));
            }

            return list;
        }

        if (raw is McsYamlTaggedScalar tagged)
        {
            return FromTaggedValue(underlying, target, tagged, key);
        }

        if (raw is not string text)
        {
            if (underlying == typeof(Guid) && Nullable.GetUnderlyingType(target) == null)
            {
                throw new McsYamlFormatException($"Property '{key}' expects a unique identifier but the document contains no value.");
            }

            return underlying.IsValueType && Nullable.GetUnderlyingType(target) == null ? Activator.CreateInstance(underlying) : null;
        }

        if (underlying == typeof(string))
        {
            return text;
        }

        if (typeof(IEnumerable).IsAssignableFrom(underlying))
        {
            throw new McsYamlFormatException($"Property '{key}' expects a sequence but the document contains '{text}'.");
        }

        if (underlying == typeof(bool))
        {
            return ParseBoolean(text, key);
        }

        if (underlying == typeof(Guid))
        {
            return Guid.TryParse(text, out var identifier)
                ? identifier
                : throw new McsYamlFormatException($"Property '{key}' expects a unique identifier but the document contains '{text}'.");
        }

        if (underlying.IsEnum)
        {
            return Enum.IsDefined(underlying, text)
                ? Enum.Parse(underlying, text, true)
                : throw new McsYamlFormatException($"Property '{key}' does not allow the value '{text}'.");
        }

        if (IsIntegerType(underlying))
        {
            return ConvertInteger(underlying, text, key);
        }

        try
        {
            return Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new McsYamlFormatException($"Property '{key}' expects {DescribeType(underlying)} but the document contains '{text}'.");
        }
    }

    private static object? FromTaggedValue(Type underlying, Type target, McsYamlTaggedScalar tagged, string key)
    {
        if (tagged.Value is string text)
        {
            if (IsIntegerType(underlying))
            {
                return ConvertTaggedInteger(underlying, text, key);
            }

            return underlying == typeof(bool)
                ? ConvertTaggedBoolean(text, key)
                : FromDocumentValue(target, text, key, rejectUnknownProperties: false);
        }

        try
        {
            return Convert.ChangeType(tagged.Value, underlying, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new McsYamlFormatException($"Property '{key}' expects {DescribeType(underlying)} but the document contains '{tagged.Text}'.");
        }
    }

    private static object ConvertTaggedInteger(Type underlying, string text, string key)
    {
        try
        {
            return text.StartsWith("0x", StringComparison.Ordinal)
                ? Convert.ChangeType(Convert.ToInt64(text.Substring(2), 16), underlying, CultureInfo.InvariantCulture)
                : Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new McsYamlFormatException($"Property '{key}' expects a number but the document contains '{text}'.");
        }
    }

    private static bool ConvertTaggedBoolean(string text, string key)
    {
        if (bool.TryParse(text.Trim(), out var flag))
        {
            return flag;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number != 0
            : throw new McsYamlFormatException($"Property '{key}' expects a boolean but the document contains '{text}'.");
    }

    private static bool ParseBoolean(string text, string key) => McsYamlScalars.TryParseBoolean(text, out var value)
        ? value
        : throw new McsYamlFormatException($"Property '{key}' expects a boolean but the document contains '{text}'.");

    private static object ConvertInteger(Type underlying, string text, string key)
    {
        if (!McsYamlScalars.TryParseInteger(text, out var magnitude))
        {
            throw new McsYamlFormatException($"Property '{key}' expects a number but the document contains '{text}'.");
        }

        try
        {
            return Convert.ChangeType(magnitude, underlying, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new McsYamlFormatException($"Property '{key}' cannot hold the value '{text}'.");
        }
    }

    private static bool IsIntegerType(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

    private static string DescribeType(Type type) => type == typeof(Guid) ? "a unique identifier" : "a " + type.Name.ToLowerInvariant();

    private static TypeBindings GetBindings(Type type)
    {
        lock (BindingCache)
        {
            if (BindingCache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.CanWrite && property.GetCustomAttribute<McsYamlIgnoreAttribute>() == null)
                .Select(property => new PropertyBinding(SerializeName(property.Name), property))
                .ToArray();

            var bindings = new TypeBindings(properties, new HashSet<string>(properties.Select(binding => binding.Key), StringComparer.Ordinal));
            BindingCache[type] = bindings;
            return bindings;
        }
    }

    private readonly struct TypeBindings
    {
        public TypeBindings(PropertyBinding[] properties, HashSet<string> keys)
        {
            Properties = properties;
            Keys = keys;
        }

        public PropertyBinding[] Properties { get; }

        public HashSet<string> Keys { get; }
    }

    private readonly struct PropertyBinding
    {
        public PropertyBinding(string key, PropertyInfo property)
        {
            Key = key;
            Property = property;
        }

        public string Key { get; }

        public PropertyInfo Property { get; }
    }
}
