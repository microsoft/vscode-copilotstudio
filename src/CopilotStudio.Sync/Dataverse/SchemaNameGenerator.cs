// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

// Original copilot studio code: https://msazure.visualstudio.com/OneAgile/_git/power-platform-ux?path=%2Fpackages%2Fpowerva-core%2Fsrc%2Futils%2FschemaName.ts&version=GBmaster&_a=contents
public static class SchemaNameGenerator
{
    /// <summary>The publisher prefix a new agent's schema name starts with when no other prefix is chosen.</summary>
    public const string DefaultPublisherPrefix = "cr";

    internal const string LetterAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private const string AlphanumericAlphabet = LetterAlphabet + "0123456789";
    private const int SCHEMA_NAME_SUFFIX_LENGTH = 6;
    private const int MAX_COLLISIONS = 20;

    // Matches anything not alphanumeric or not one of: '_', '-', '.', '{', '}', '!'
    private static readonly Regex SCHEMA_NAME_REGEX = new(@"[^A-Za-z_\-.{}!0-9]*", RegexOptions.Compiled);

    private static readonly Regex PublisherPrefixRegex = new("^[A-Za-z][A-Za-z0-9]{1,7}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a publisher prefix is two to eight ASCII letters or digits starting with a letter.</summary>
    /// <param name="publisherPrefix">The publisher prefix to check.</param>
    /// <returns><see langword="true"/> when the prefix is valid.</returns>
    public static bool IsValidPublisherPrefix(string? publisherPrefix) => publisherPrefix is not null && PublisherPrefixRegex.IsMatch(publisherPrefix);

    /// <summary>The publisher prefix a schema name starts with.</summary>
    /// <param name="schemaName">The schema name to read.</param>
    /// <returns>The text before the first underscore when it is a valid publisher prefix; otherwise <see langword="null"/>.</returns>
    public static string? PublisherPrefixOf(string? schemaName)
    {
        var separator = schemaName?.IndexOf('_') ?? -1;
        var prefix = schemaName is not null && separator > 0 ? schemaName.Substring(0, separator) : null;

        return IsValidPublisherPrefix(prefix) ? prefix : null;
    }

    /// <summary>Derives an agent schema name from a publisher prefix and a display name.</summary>
    /// <param name="publisherPrefix">A valid publisher prefix.</param>
    /// <param name="displayName">The display name whose ASCII letters and digits form the name part.</param>
    /// <param name="maximumLength">The longest schema name to return; the name part is truncated to fit.</param>
    /// <returns>The schema name, or <see langword="null"/> when the display name has no ASCII letter or digit or not one of them fits.</returns>
    /// <exception cref="ArgumentException">The publisher prefix is not valid.</exception>
    public static string? GenerateAgentSchemaName(string publisherPrefix, string displayName, int maximumLength = SchemaNameValidator.MaximumLength)
    {
        if (!IsValidPublisherPrefix(publisherPrefix))
        {
            throw new ArgumentException($"The publisher prefix '{publisherPrefix}' is not 2 to 8 letters or digits starting with a letter.", nameof(publisherPrefix));
        }

        var root = publisherPrefix.ToLowerInvariant() + "_";
        var name = new string(displayName.Where(AsciiCharacters.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(maximumLength - root.Length).ToArray());

        return name.Length == 0 ? null : root + name;
    }

    /// <summary>
    /// Generates a valid unique schema name for a bot component.
    /// </summary>
    /// <param name="botSchemaPrefix">The prefix for the bot schema.</param>
    /// <param name="componentPrefix">The prefix for the component.</param>
    /// <param name="componentDisplayName">The display name of the component.</param>
    /// <param name="existingSchemaNames">A collection of existing schema names to avoid collisions with.</param>
    /// <param name="alwaysAddCollisionSuffix">Whether to always add a collision suffix.</param>
    /// <returns>A valid unique schema name.</returns>
    public static string GenerateSchemaNameForBotComponent(
        string botSchemaPrefix,
        string componentPrefix,
        string componentDisplayName,
        IEnumerable<string> existingSchemaNames,
        bool alwaysAddCollisionSuffix = false)
    {
        var baseSchemaName = GenerateBaseSchemaName(botSchemaPrefix, componentDisplayName, componentPrefix);
        return ResolveSchemaNameCollisions(baseSchemaName, existingSchemaNames, alwaysAddCollisionSuffix, () => $"_{RandomString(3, AlphanumericAlphabet)}");
    }

    internal static string RandomString(int length, string alphabet)
    {
        // #if kept: net10's RandomNumberGenerator.GetInt32 is a native implementation
        // that avoids managed buffer allocation. The netstandard2.0 polyfill performs
        // rejection sampling in managed code with a per-call 4-byte buffer alloc -- a
        // small but real cost on net10 if we LCD'd here. Keeping the dispatch.
#if NETSTANDARD2_0
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[RandomNumberGeneratorPolyfill.GetInt32(alphabet.Length)]).ToArray());
#else
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
#endif
    }

    /// <summary>
    /// Cleans a schema name by removing invalid characters and trimming to max length.
    /// </summary>
    private static string CleanSchemaName(string schemaName, int lengthReservation = 0)
    {
        var cleaned = SCHEMA_NAME_REGEX.Replace(schemaName, "");

        return cleaned.Length > SchemaNameValidator.MaximumLength - lengthReservation ? cleaned.Substring(0, SchemaNameValidator.MaximumLength - lengthReservation) : cleaned;
    }

    /// <summary>
    /// Generates the base schema name for a component.
    /// </summary>
    private static string GenerateBaseSchemaName(string botSchemaPrefix, string componentDisplayName, string componentPrefix)
    {
        var cleanDisplayName = CleanSchemaName(componentDisplayName);

        if (string.IsNullOrEmpty(cleanDisplayName))
        {
            cleanDisplayName = RandomString(3, AlphanumericAlphabet);
        }

        return CleanSchemaName($"{botSchemaPrefix}.{componentPrefix}.{cleanDisplayName}");
    }

    /// <summary>
    /// Resolves schema name collisions by adding a suffix if needed.
    /// </summary>
    private static string ResolveSchemaNameCollisions(
        string baseSchemaName,
        IEnumerable<string> existingSchemaNames,
        bool alwaysAddCollisionSuffix = false,
        Func<string>? suffixGenerator = null)
    {
        suffixGenerator ??= () => $"_{RandomString(SCHEMA_NAME_SUFFIX_LENGTH, AlphanumericAlphabet)}";
        var existing = new HashSet<string>(existingSchemaNames.Select(n => n.ToUpperInvariant()));
        var schemaName = baseSchemaName;
        var collisionSuffix = alwaysAddCollisionSuffix ? suffixGenerator() : "";
        var attempts = MAX_COLLISIONS;
        var isCollision = true;

        while (isCollision && attempts > 0)
        {
            schemaName = CleanSchemaName(CleanSchemaName(baseSchemaName, collisionSuffix.Length) + collisionSuffix);
            isCollision = existing.Contains(schemaName.ToUpperInvariant());
            if (isCollision)
            {
                collisionSuffix = suffixGenerator();
            }
            attempts--;
        }

        return CleanSchemaName(schemaName);
    }
}
