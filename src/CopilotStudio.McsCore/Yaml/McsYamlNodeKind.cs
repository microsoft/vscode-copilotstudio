// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Identifies whether a node holds a scalar, a mapping or a sequence.</summary>
internal enum McsYamlNodeKind
{
    Scalar,
    Mapping,
    Sequence,
}
