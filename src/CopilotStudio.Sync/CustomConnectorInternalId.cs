// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Reads and predicts the internal identifiers Power Platform gives custom connectors.</summary>
public static class CustomConnectorInternalId
{
    internal const string PlatformPrefix = "shared_";

    private const string SuffixSeparator = "-5f";

    private const int SuffixLength = 16;

    /// <summary>The part of an internal identifier that is the same in every environment.</summary>
    /// <param name="internalId">The connector internal identifier.</param>
    /// <returns>The identifier up to and including the separator before its environment suffix, or <see langword="null"/> when it carries no environment suffix.</returns>
    public static string? StablePrefixOf(string? internalId) => internalId is not null && SuffixStart(internalId) is { } start ? internalId.Substring(0, start) : null;

    /// <summary>The environment suffix an internal identifier ends with.</summary>
    /// <param name="internalId">The connector internal identifier.</param>
    /// <returns>The sixteen hexadecimal characters after the last separator, in lowercase, or <see langword="null"/> when there are none.</returns>
    public static string? EnvironmentSuffixOf(string? internalId) => internalId is not null && SuffixStart(internalId) is { } start ? internalId.Substring(start).ToLowerInvariant() : null;

    /// <summary>The internal identifier an environment gives a custom connector it creates.</summary>
    /// <param name="name">The connector name, which starts with its publisher prefix and an underscore.</param>
    /// <param name="displayName">The connector display name.</param>
    /// <param name="environmentSuffix">The environment's suffix.</param>
    /// <returns>The internal identifier.</returns>
    /// <exception cref="ArgumentException">The name does not start with a publisher prefix and an underscore.</exception>
    public static string Derive(string name, string displayName, string environmentSuffix)
    {
        var builder = new StringBuilder(PlatformPrefix);

        foreach (var character in $"{SchemaNameGenerator.PublisherPrefixOf(name) ?? throw new ArgumentException($"The custom connector name '{name}' does not start with a publisher prefix and an underscore.", nameof(name))}_{displayName}_".ToLowerInvariant())
        {
            if (AsciiCharacters.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('-').Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        return builder.Append(environmentSuffix).ToString();
    }

    /// <summary>The environment suffix of an internal identifier that the environment derived from the connector's name and display name.</summary>
    /// <param name="name">The connector name.</param>
    /// <param name="displayName">The connector display name.</param>
    /// <param name="internalId">The connector internal identifier.</param>
    /// <returns>The suffix, or <see langword="null"/> when the identifier is not the one the environment derives from the name and display name.</returns>
    public static string? ConformingSuffixOf(string name, string displayName, string internalId) => SchemaNameGenerator.PublisherPrefixOf(name) is not null && EnvironmentSuffixOf(internalId) is { } suffix && string.Equals(internalId, Derive(name, displayName, suffix), StringComparison.OrdinalIgnoreCase) ? suffix : null;

    private static int? SuffixStart(string internalId)
    {
        var separator = internalId.LastIndexOf(SuffixSeparator, StringComparison.OrdinalIgnoreCase);
        var start = separator + SuffixSeparator.Length;

        return separator > 0 && internalId.Length - start == SuffixLength && internalId.Skip(start).All(Uri.IsHexDigit) ? start : null;
    }
}
