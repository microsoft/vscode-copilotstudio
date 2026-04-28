// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

// Original copilot studio code: https://msazure.visualstudio.com/OneAgile/_git/power-platform-ux?path=%2Fpackages%2Fpowerva-core%2Fsrc%2Futils%2FschemaName.ts&version=GBmaster&_a=contents
public static class SchemaNameGenerator
{
    private const int SCHEMA_NAME_SUFFIX_LENGTH = 6;
    private const int MAX_SCHEMA_NAME_LENGTH = 100;
    private const int MAX_COLLISIONS = 20;

    // Matches anything not alphanumeric or not one of: '_', '-', '.', '{', '}', '!'
    private static readonly Regex SCHEMA_NAME_REGEX = new(@"[^A-Za-z_\-.{}!0-9]*", RegexOptions.Compiled);

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
        return ResolveSchemaNameCollisions(baseSchemaName, existingSchemaNames, alwaysAddCollisionSuffix, () => $"_{RandId(3)}");
    }

    /// <summary>
    /// Generates a random alphanumeric ID of specified length.
    /// </summary>
    /// <param name="length">The length of the ID to generate.</param>
    /// <returns>A random alphanumeric ID.</returns>
    private static string RandId(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
#if NETSTANDARD2_0
        return new string(Enumerable.Range(0, length).Select(_ => chars[RandomNumberGeneratorPolyfill.GetInt32(chars.Length)]).ToArray());
#else
        return new string(Enumerable.Range(0, length).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
#endif
    }

    /// <summary>
    /// Cleans a schema name by removing invalid characters and trimming to max length.
    /// </summary>
    private static string CleanSchemaName(string schemaName, int lengthReservation = 0)
    {
        var cleaned = SCHEMA_NAME_REGEX.Replace(schemaName, "");

        return cleaned.Length > MAX_SCHEMA_NAME_LENGTH - lengthReservation ? cleaned.Substring(0, MAX_SCHEMA_NAME_LENGTH - lengthReservation) : cleaned;
    }

    /// <summary>
    /// Generates the base schema name for a component.
    /// </summary>
    private static string GenerateBaseSchemaName(string botSchemaPrefix, string componentDisplayName, string componentPrefix)
    {
        var cleanDisplayName = CleanSchemaName(componentDisplayName);

        if (string.IsNullOrEmpty(cleanDisplayName))
        {
            cleanDisplayName = RandId(3);
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
        suffixGenerator ??= () => $"_{RandId(SCHEMA_NAME_SUFFIX_LENGTH)}";
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
