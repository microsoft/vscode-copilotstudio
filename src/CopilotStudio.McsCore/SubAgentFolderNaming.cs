// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Derives the on-disk folder name for a sub-agent (<c>AgentDialog</c>) from its
/// display name.
/// </summary>
/// <remarks>
/// <para>Sub-agents created in the Copilot Studio web canvas get a machine-generated
/// schema name such as <c>{bot}.agent.Agent_7_8</c>. Projecting that schema short-name
/// onto disk produces unfriendly folders like <c>agents/Agent_7_8/</c>. Instead we
/// project the (sanitized) <b>display name</b>, e.g. "Transfer Funds" =&gt;
/// <c>agents/Transfer Funds/</c> for the on-disk folder, matching the authoring layout
/// used by the CLI.</para>
/// <para>The result must be safe as a path segment on Windows, Linux, and macOS. The
/// space-stripping default (<c>keepSpaces: false</c>) additionally yields a valid schema
/// short-name, which is what <see cref="LspProjection"/> derives from the folder for
/// locally-authored sub-agents. The sanitizer is built to:</para>
/// <list type="bullet">
/// <item>Strip control characters and (unless <c>keepSpaces</c> is set) blank spaces;
/// other whitespace such as tabs and newlines is always stripped.</item>
/// <item>Keeps only alphanumerics, <c>_</c>, <c>-</c>, and printable non-ASCII letters
/// (so localized names survive, consistent with the root-agent folder sanitizer). Every
/// character Windows/Linux/macOS forbid in a path segment - <c>&lt; &gt; : " / \ | ? *</c>,
/// control codes, and the leftover ASCII punctuation - is dropped.</item>
/// <item>Never ends in a dot or space (those are stripped), which Windows silently trims.</item>
/// <item>Avoids the Windows reserved device names (<c>CON</c>, <c>PRN</c>, <c>NUL</c>,
/// <c>COM1</c>...) by appending <c>_</c> so a sub-agent literally named "CON" still gets
/// a usable folder.</item>
/// <item>Caps the segment length so it stays within filesystem component limits.</item>
/// </list>
/// <para>When nothing usable remains the method returns <see langword="null"/> so the
/// caller falls back to the schema short-name; behavior never regresses below today's
/// projection.</para>
/// </remarks>
internal static class SubAgentFolderNaming
{
    // Filesystem component limits are 255 chars/bytes on Windows/Linux/macOS. Cap well
    // under that (and aligned with the schema-name length budget) so even multi-byte
    // names stay safe and the folder remains a valid schema short-name for local authoring.
    private const int MaxFolderNameLength = 100;

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="name"/> is a Windows reserved
    /// device name. Reserved at every path level, case-insensitively, with or without an
    /// extension: <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, and <c>COM</c>/<c>LPT</c>
    /// followed by a single port digit <c>0-9</c> or its superscript form
    /// (<c>\u00B9 \u00B2 \u00B3</c>, i.e. <c>COM\u00B9</c> which resolves to <c>COM1</c>).
    /// </summary>
    /// <remarks>
    /// Enumerating the COM/LPT ranges by pattern (rather than a hardcoded literal set) keeps
    /// the check small and covers the full official list - including <c>COM0</c>/<c>LPT0</c>
    /// and the superscript variants, which survive sanitization because they are non-ASCII.
    /// The check is purely logical so the outcome is deterministic across Windows/Linux/macOS
    /// (a name sanitized on one OS must be safe on Windows), rather than relying on the host
    /// filesystem, whose device-name handling differs by OS and .NET version.
    /// </remarks>
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
    /// Returns a cross-platform-safe folder name derived from <paramref name="displayName"/>,
    /// or <see langword="null"/> when the display name is empty or contains no usable
    /// characters (caller should fall back to the schema-derived short name).
    /// </summary>
    /// <remarks>
    /// This overload strips every blank space (along with other non-schema-safe
    /// characters), so the result is safe as both a folder segment and a schema
    /// short-name. Use <see cref="FromDisplayName(string?, bool)"/> with
    /// <c>keepSpaces: true</c> when projecting the human-readable on-disk folder name.
    /// </remarks>
    public static string? FromDisplayName(string? displayName) => FromDisplayName(displayName, keepSpaces: false);

    /// <summary>
    /// Returns a cross-platform-safe folder name derived from <paramref name="displayName"/>,
    /// or <see langword="null"/> when the display name is empty or contains no usable
    /// characters (caller should fall back to the schema-derived short name).
    /// </summary>
    /// <param name="displayName">The sub-agent display name to project.</param>
    /// <param name="keepSpaces">
    /// When <see langword="true"/>, internal blank spaces are preserved so the on-disk
    /// folder stays human-readable (e.g. <c>Transfer Funds</c>); leading and trailing
    /// spaces are still trimmed because Windows silently drops them from a path segment.
    /// When <see langword="false"/> (used for schema short-names) every space is removed
    /// along with the other non-schema-safe characters.
    /// </param>
    public static string? FromDisplayName(string? displayName, bool keepSpaces)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var builder = new StringBuilder(displayName!.Length);
        foreach (var c in displayName)
        {
            // Preserve the regular space character only when requested; every other
            // whitespace character and all control characters are always stripped.
            if (keepSpaces && c == ' ')
            {
                builder.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                continue;
            }

            // Keep alphanumerics, underscore, hyphen, and printable non-ASCII characters
            // (letters/digits in other scripts). Everything else - path separators, drive
            // colons, wildcards and other ASCII punctuation - is dropped so the segment is
            // safe as both a file path and a schema short-name on every OS.
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c > 127)
            {
                builder.Append(c);
            }
        }

        if (builder.Length > MaxFolderNameLength)
        {
            builder.Length = MaxFolderNameLength;
        }

        // Trim leading/trailing spaces: Windows silently trims them from a path segment,
        // so a folder name must neither start nor end with one. This is a no-op when
        // spaces were stripped entirely (keepSpaces == false).
        var result = builder.ToString().Trim(' ');

        if (result.Length == 0)
        {
            return null;
        }

        // A name that collides with a Windows reserved device name cannot be used as a
        // directory on Windows; disambiguate with a trailing underscore (still a valid,
        // deterministic schema short-name).
        if (IsWindowsReservedName(result))
        {
            result += "_";
        }

        return result;
    }
}
