// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>A scalar that carried an explicit standard tag, keeping both the tag and the value it denotes.</summary>
internal sealed class McsYamlTaggedScalar : IEquatable<McsYamlTaggedScalar>
{
    public McsYamlTaggedScalar(string tag, string text, object value)
    {
        Tag = tag;
        Text = text;
        Value = value;
    }

    public string Tag { get; }

    public string Text { get; }

    public object Value { get; }

    public bool Equals(McsYamlTaggedScalar? other) => other != null
        && string.Equals(Tag, other.Tag, StringComparison.Ordinal)
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? other) => Equals(other as McsYamlTaggedScalar);

    public override int GetHashCode() => Tag.GetHashCode() ^ Text.GetHashCode();

    public override string ToString() => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty;
}
