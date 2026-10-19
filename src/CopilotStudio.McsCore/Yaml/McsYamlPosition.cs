// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>A one-based line and column plus the absolute character offset in the source text.</summary>
internal readonly struct McsYamlPosition
{
    public McsYamlPosition(int line, int column, int index)
    {
        Line = line;
        Column = column;
        Index = index;
    }

    public int Line { get; }

    public int Column { get; }

    public int Index { get; }
}
