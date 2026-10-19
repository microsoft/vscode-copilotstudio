// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>A single key and value entry of a mapping node.</summary>
internal sealed class McsYamlProperty
{
    public McsYamlProperty(McsYamlNode key, McsYamlNode value)
    {
        Key = key;
        Value = value;
    }

    public McsYamlNode Key { get; }

    public McsYamlNode Value { get; }

    public string Name => Key.Scalar ?? string.Empty;

    public McsYamlPosition NameStart => Key.Start;

    public McsYamlPosition NameEnd => Key.End;
}
