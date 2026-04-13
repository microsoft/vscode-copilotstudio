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

    private const int MaxSchemaNameLength = 100;

    /// <summary>
    /// Check if schema name is syntactically valid.
    /// </summary>
    public static bool IsValid(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName) || schemaName.Length > MaxSchemaNameLength)
        {
            return false;
        }

        return ValidSchemaNameRegex.IsMatch(schemaName);
    }
}
