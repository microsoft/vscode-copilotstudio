// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>A scalar, mapping or sequence node together with its source span.</summary>
internal sealed class McsYamlNode
{
    private readonly McsYamlNode? _target;

    private readonly string? _scalar;

    private readonly IReadOnlyList<McsYamlProperty>? _properties;

    private readonly IReadOnlyList<McsYamlNode>? _items;

    private McsYamlNode(McsYamlNodeKind kind, string? scalar, IReadOnlyList<McsYamlProperty>? properties, IReadOnlyList<McsYamlNode>? items, McsYamlNode? target, McsYamlPosition start, McsYamlPosition end)
    {
        Kind = kind;
        _scalar = scalar;
        _properties = properties;
        _items = items;
        _target = target;
        Start = start;
        End = end;
    }

    public McsYamlNodeKind Kind { get; }

    public string? Scalar => _target != null ? _target.Scalar : _scalar;

    public IReadOnlyList<McsYamlProperty>? Properties => _target != null ? _target.Properties : _properties;

    public IReadOnlyList<McsYamlNode>? Items => _target != null ? _target.Items : _items;

    public string? Tag => _target != null ? _target.Tag : _tag;

    public McsYamlPosition Start { get; }

    public McsYamlPosition End { get; }

    private string? _tag;

    public static McsYamlNode ForScalar(string? scalar, McsYamlPosition start, McsYamlPosition end) => new(McsYamlNodeKind.Scalar, scalar, null, null, null, start, end);

    public static McsYamlNode ForTaggedScalar(string? scalar, McsYamlPosition start, McsYamlPosition end, string tag) => new(McsYamlNodeKind.Scalar, scalar, null, null, null, start, end) { _tag = tag };

    public static McsYamlNode ForMapping(IReadOnlyList<McsYamlProperty> properties, McsYamlPosition start, McsYamlPosition end) => new(McsYamlNodeKind.Mapping, null, properties, null, null, start, end);

    public static McsYamlNode ForSequence(IReadOnlyList<McsYamlNode> items, McsYamlPosition start, McsYamlPosition end) => new(McsYamlNodeKind.Sequence, null, null, items, null, start, end);

    public static McsYamlNode ForAlias(McsYamlNode target, McsYamlPosition start, McsYamlPosition end, int subtreeCount, int subtreeDepth) => new(target.Kind, null, null, null, target, start, end)
    {
        SubtreeCount = subtreeCount,
        SubtreeDepth = subtreeDepth,
    };

    public bool IsAlias => _target != null;

    public int SubtreeCount { get; private init; }

    public int SubtreeDepth { get; private init; }

    public object? ToValue() => ToValue(preserveStringTags: false);

    /// <summary>Converts the node to plain values, optionally keeping explicitly string-tagged scalars distinguishable for typed binding.</summary>
    public object? ToValue(bool preserveStringTags)
    {
        var properties = Properties;
        if (properties != null)
        {
            var map = new Dictionary<string, object?>(properties.Count, StringComparer.Ordinal);
            foreach (var property in properties)
            {
                map[property.Name] = property.Value.ToValue(preserveStringTags);
            }

            return map;
        }

        var items = Items;
        if (items != null)
        {
            var list = new List<object?>(items.Count);
            foreach (var item in items)
            {
                list.Add(item.ToValue(preserveStringTags));
            }

            return list;
        }

        var scalar = Scalar;
        var tag = Tag;
        if (tag == null || scalar == null)
        {
            return scalar;
        }

        var resolved = McsYamlScalars.ResolveTaggedValue(tag, scalar);
        if (resolved is string text)
        {
            return preserveStringTags ? new McsYamlTaggedScalar(tag, scalar, text) : text;
        }

        return new McsYamlTaggedScalar(tag, scalar, resolved);
    }
}
