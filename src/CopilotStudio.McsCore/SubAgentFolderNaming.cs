// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Frozen;
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
/// <c>agents/TransferFunds/</c>, matching the authoring layout used by the CLI.</para>
/// <para>The result must be safe both as a path segment and as a schema short-name,
/// because for locally-authored sub-agents the folder name is what
/// <see cref="LspProjection"/> derives the schema name from. The sanitizer is built to
/// produce a name that is valid on Windows, Linux, and macOS:</para>
/// <list type="bullet">
/// <item>Strips whitespace and control characters.</item>
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

    // Windows reserves these device names at every path level, with or without an
    // extension (e.g. "CON", "con", "COM1"). Compared case-insensitively.
    private static readonly FrozenSet<string> WindowsReservedNames = new[]
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a cross-platform-safe folder name derived from <paramref name="displayName"/>,
    /// or <see langword="null"/> when the display name is empty or contains no usable
    /// characters (caller should fall back to the schema-derived short name).
    /// </summary>
    public static string? FromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var builder = new StringBuilder(displayName!.Length);
        foreach (var c in displayName)
        {
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

        if (builder.Length == 0)
        {
            return null;
        }

        if (builder.Length > MaxFolderNameLength)
        {
            builder.Length = MaxFolderNameLength;
        }

        var result = builder.ToString();

        // A name that collides with a Windows reserved device name cannot be used as a
        // directory on Windows; disambiguate with a trailing underscore (still a valid,
        // deterministic schema short-name).
        if (WindowsReservedNames.Contains(result))
        {
            result += "_";
        }

        return result;
    }
}
