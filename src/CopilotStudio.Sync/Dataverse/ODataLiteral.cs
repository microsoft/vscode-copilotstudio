// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Writes values as OData string literals for use in a filter.</summary>
public static class ODataLiteral
{
    /// <summary>Wraps a value in single quotes, doubling every quote it holds, for an equality comparison.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The quoted literal.</returns>
    public static string Quote(string value) => value is null ? throw new ArgumentNullException(nameof(value)) : $"'{value.Replace("'", "''")}'";

    /// <summary>Quotes a value for <c>contains</c> or <c>startswith</c>, escaping the characters Dataverse would otherwise treat as wildcards.</summary>
    /// <param name="value">The raw text to match literally.</param>
    /// <returns>The quoted literal, matching only the text itself.</returns>
    public static string LikePattern(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var escaped = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character is '[' or '%' or '_')
            {
                escaped.Append('[').Append(character).Append(']');
            }
            else
            {
                escaped.Append(character);
            }
        }

        return Quote(escaped.ToString());
    }
}
