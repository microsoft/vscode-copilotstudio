// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Decides whether a name is one Dataverse accepts for a workflow.</summary>
public static class WorkflowNaming
{
    /// <summary>The most characters a workflow name may hold, which is the Dataverse limit for <c>workflow.name</c>.</summary>
    public const int MaximumLength = 100;

    /// <summary>Explains why a workflow name is unusable, or returns <see langword="null"/> when it is usable.</summary>
    /// <param name="name">The workflow name to check.</param>
    /// <returns>The reason the name is refused, or <see langword="null"/> when it is valid.</returns>
    public static string? Validate(string? name)
    {
        var length = name?.Trim().Length ?? 0;

        if (name is null || length == 0)
        {
            return "A workflow name is required.";
        }

        if (length > MaximumLength)
        {
            return $"A workflow name can hold at most {MaximumLength} characters, and this one holds {length}.";
        }

        return name.Any(char.IsControl) ? "A workflow name cannot hold control characters such as tabs or line breaks." : null;
    }
}
