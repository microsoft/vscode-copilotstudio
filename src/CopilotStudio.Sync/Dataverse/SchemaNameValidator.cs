// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Dataverse/SchemaNameValidator.cs

using System.Text.RegularExpressions;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>
/// Validates schema names for Dataverse agents.
/// </summary>
public static class SchemaNameValidator
{
    private static readonly Regex ValidSchemaNameRegex = new(@"^[A-Za-z0-9_\-\.{}!]+$", RegexOptions.Compiled);

    /// <summary>The longest schema name Dataverse accepts for an agent or a component.</summary>
    public const int MaximumLength = 100;

    /// <summary>
    /// Check if schema name is syntactically valid.
    /// </summary>
    public static bool IsValid(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName) || schemaName.Length > MaximumLength)
        {
            return false;
        }

        return ValidSchemaNameRegex.IsMatch(schemaName);
    }
}
