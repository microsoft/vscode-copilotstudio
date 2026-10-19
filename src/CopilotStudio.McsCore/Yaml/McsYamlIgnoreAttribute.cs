// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Excludes a property from metadata sidecar serialization.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class McsYamlIgnoreAttribute : Attribute
{
}
