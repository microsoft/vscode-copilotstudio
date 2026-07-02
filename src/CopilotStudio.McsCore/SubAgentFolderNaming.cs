// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Derives the on-disk folder name for a sub-agent (<c>AgentDialog</c>) from its display
/// name, e.g. "Transfer Funds" =&gt; <c>agents/Transfer Funds/</c>.
/// </summary>
/// <remarks>
/// The result is a path segment valid on Windows, Linux, and macOS: control chars (and, by
/// default, spaces) are stripped, only alphanumerics/<c>_</c>/<c>-</c>/printable non-ASCII are
/// kept, leading/trailing spaces are trimmed, and Windows reserved device names get a trailing
/// <c>_</c>. With <c>keepSpaces: false</c> it also yields a valid schema short-name (what
/// <see cref="LspProjection"/> derives back from the folder). Returns <see langword="null"/>
/// when nothing usable remains, so the caller falls back to the schema short-name.
/// </remarks>
internal static class SubAgentFolderNaming
{
    // Filesystem component limit is 255; cap well under it (and within the schema-name budget).
    private const int MaxFolderNameLength = 100;

    /// <summary>
    /// True if <paramref name="name"/> is a Windows reserved device name: <c>CON</c>,
    /// <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, or <c>COM</c>/<c>LPT</c> + a port digit <c>0-9</c>
    /// or its superscript form (<c>\u00B9\u00B2\u00B3</c>). Checked by pattern (not a literal
    /// set) so it stays deterministic across OSes and covers COM0/LPT0 and the superscripts.
    /// </summary>
    private static bool IsWindowsReservedName(string name)
    {
        if (name.Length == 3)
        {
            return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        if (name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            var portChar = name[3];
            return portChar is (>= '0' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3';
        }

        return false;
    }

    /// <summary>
    /// Sanitizes <paramref name="displayName"/> into a folder/schema-safe name (spaces
    /// stripped), or <see langword="null"/> when nothing usable remains.
    /// </summary>
    public static string? FromDisplayName(string? displayName) => FromDisplayName(displayName, keepSpaces: false);

    /// <summary>
    /// Sanitizes <paramref name="displayName"/> into a cross-platform-safe folder name, or
    /// <see langword="null"/> when nothing usable remains. With <paramref name="keepSpaces"/>
    /// internal spaces are kept for a human-readable folder (ends are still trimmed);
    /// otherwise every space is stripped for a valid schema short-name.
    /// </summary>
    public static string? FromDisplayName(string? displayName, bool keepSpaces)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var builder = new StringBuilder(displayName!.Length);
        foreach (var c in displayName)
        {
            if (keepSpaces && c == ' ')
            {
                builder.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                continue;
            }

            // Keep alphanumerics, _, -, and printable non-ASCII (localized names); drop the
            // rest (path separators, wildcards, other ASCII punctuation).
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c > 127)
            {
                builder.Append(c);
            }
        }

        if (builder.Length > MaxFolderNameLength)
        {
            builder.Length = MaxFolderNameLength;
        }

        // Windows trims trailing/leading spaces from path segments; no-op when keepSpaces is false.
        var result = builder.ToString().Trim(' ');

        if (result.Length == 0)
        {
            return null;
        }

        if (IsWindowsReservedName(result))
        {
            result += "_";
        }

        return result;
    }
}
