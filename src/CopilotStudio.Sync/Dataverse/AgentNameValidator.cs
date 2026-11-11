// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Checks agent display names against the rules Copilot Studio enforces.</summary>
public static class AgentNameValidator
{
    /// <summary>The longest agent display name Copilot Studio accepts.</summary>
    public const int MaximumLength = 42;

    private static readonly char[] AngleBrackets = { '<', '>' };

    /// <summary>Describes why Copilot Studio would refuse a display name.</summary>
    /// <param name="displayName">The display name to check.</param>
    /// <returns><see langword="null"/> when the name is valid; otherwise a clause that reads after a subject naming the display name.</returns>
    public static string? FindProblem(string? displayName)
    {
        var trimmed = displayName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "is required";
        }

        if (trimmed.Length > MaximumLength)
        {
            return $"is {trimmed.Length} characters long; Copilot Studio allows at most {MaximumLength}";
        }

        return trimmed.IndexOfAny(AngleBrackets) >= 0 ? "contains < or >, which Copilot Studio does not allow" : null;
    }
}
