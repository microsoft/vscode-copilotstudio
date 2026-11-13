// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;
using Microsoft.Agents.Platform.Content.Exceptions;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>A row Dataverse refused to create because another row already holds its key.</summary>
/// <param name="EntityLogicalName">The logical name of the entity whose row was refused, such as <c>botcomponent</c>.</param>
/// <param name="KeyName">The key the existing row holds, when the service named it.</param>
/// <param name="KeyValue">The key value the existing row holds, when the service named it.</param>
public sealed record DataverseKeyConflict(string EntityLogicalName, string? KeyName, string? KeyValue);

/// <summary>Classifies the failures Dataverse reports for a write.</summary>
public static class DataverseFailureClassifier
{
    private const string KeyConflictCode = "0x80072013";

    private const string KeyConflictName = "ExportKeyInvalidCreate";

    private const string ObjectDoesNotExistCode = "0x80040217";

    private const string MissingRowMarker = "does not exist";

    private static readonly Regex CreationPattern = new(@"Cannot complete the creation of (?<entity>[A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ViolatedKeyPattern = new(@"The violation happens on the key (?<key>[A-Za-z0-9_]+):\s*(?<value>.+?)\.?(?:\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EntitySetPattern = new(@"/api/data/v[0-9.]+/(?<set>[A-Za-z0-9_]*s)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Finds the duplicate-key refusal an exception or one of its inner exceptions reports.</summary>
    /// <param name="failure">The failure to classify.</param>
    /// <returns>The refused row's entity and key, or <see langword="null"/> when the chain reports no duplicate key of a known entity.</returns>
    public static DataverseKeyConflict? FindKeyConflict(Exception failure)
    {
        var messages = Chain(failure).Select(current => current.Message).ToArray();
        var creation = messages.Select(message => CreationPattern.Match(message)).FirstOrDefault(match => match.Success);

        if (creation is null && !messages.Any(message => message.Contains(KeyConflictName, StringComparison.OrdinalIgnoreCase) || message.Contains(KeyConflictCode, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var violatedKey = messages.Select(message => ViolatedKeyPattern.Match(message)).FirstOrDefault(match => match.Success);
        var entityLogicalName = creation?.Groups["entity"].Value.ToLowerInvariant() ?? messages.Select(message => EntitySetPattern.Match(message)).Where(match => match.Success).Select(match => SingularOf(match.Groups["set"].Value.ToLowerInvariant())).FirstOrDefault();

        return entityLogicalName is null ? null : new DataverseKeyConflict(entityLogicalName, violatedKey?.Groups["key"].Value, violatedKey?.Groups["value"].Value);
    }

    /// <summary>Whether an exception or one of its inner exceptions is Dataverse reporting that a row of an entity does not exist.</summary>
    /// <param name="failure">The failure to classify.</param>
    /// <param name="entityLogicalName">The logical name of the entity the missing row belongs to, such as <c>connectionreference</c>.</param>
    /// <returns><see langword="true"/> when a not-found failure in the chain names the entity.</returns>
    public static bool IsMissingRow(Exception failure, string entityLogicalName) => Chain(failure).Any(current => (current is DataverseNotFoundException || (current is DataverseException dataverse && string.Equals(dataverse.ErrorCodeValue, ObjectDoesNotExistCode, StringComparison.OrdinalIgnoreCase))) && current.Message.Contains(entityLogicalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an exception or one of its inner exceptions says that a row it needed does not exist.</summary>
    /// <param name="failure">The failure to classify.</param>
    /// <returns><see langword="true"/> when a message in the chain says something does not exist.</returns>
    public static bool ReportsMissingRow(Exception failure) => Chain(failure).Any(current => current.Message.Contains(MissingRowMarker, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Exception> Chain(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static string SingularOf(string entitySetName) => entitySetName.EndsWith("ies", StringComparison.Ordinal) ? entitySetName.Substring(0, entitySetName.Length - 3) + "y" : entitySetName.Substring(0, entitySetName.Length - 1);
}
