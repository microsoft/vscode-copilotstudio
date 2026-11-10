// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>The names of the files at the root of an agent workspace or a component collection workspace.</summary>
public static class WorkspaceFileNames
{
    /// <summary>The settings file that declares an agent's identity and configuration.</summary>
    public const string Settings = "settings.mcs.yml";

    /// <summary>The top-level agent file, which also declares the identity of a workspace that holds no settings file.</summary>
    public const string Agent = "agent.mcs.yml";

    /// <summary>The file that declares a component collection.</summary>
    public const string Collection = "collection.mcs.yml";

    /// <summary>The file that lists the component collections an agent references.</summary>
    public const string References = "references.mcs.yml";
}