// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Restates Dataverse request failures by their HTTP status and reported error so that no raw response body reaches a caller.</summary>
public static class DataverseFailureText
{
    private static readonly Regex RawFailure = new(@"Dataverse request failed \((?<status>\d{3})\): (?<body>.*)", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The failure's message with the raw text of every Dataverse request failure it is or wraps, through inner and aggregated exceptions, replaced by that failure's description.</summary>
    /// <param name="failure">The failure to describe.</param>
    /// <returns>The failure's message without any raw Dataverse response.</returns>
    public static string Of(Exception failure) => failure is null ? throw new ArgumentNullException(nameof(failure)) : failure is DataverseRequestException dataverseFailure ? dataverseFailure.Describe() : Redact(RequestFailuresOf(failure).Aggregate(failure.Message, (text, cause) => text.Replace(cause.Message, cause.Describe())));

    /// <summary>The text with every raw Dataverse request failure it quotes, from the failure's opening words to the end of the text, replaced by that failure's description.</summary>
    /// <param name="text">The text to redact.</param>
    /// <returns>The redacted text, or an empty string when there is no text.</returns>
    public static string Redact(string? text) => RawFailure.Replace(text ?? string.Empty, match => DataverseRequestException.Describe((HttpStatusCode)int.Parse(match.Groups["status"].Value, CultureInfo.InvariantCulture), match.Groups["body"].Value));

    /// <summary>The Dataverse request failures a failure is or wraps through inner and aggregated exceptions, the failure itself first and the rest depth-first, which is the order <see cref="Of"/> describes them in.</summary>
    /// <param name="failure">The failure to search.</param>
    /// <returns>The Dataverse request failures found, or an empty list when there is none.</returns>
    public static IReadOnlyList<DataverseRequestException> RequestFailuresOf(Exception failure) => failure is null ? throw new ArgumentNullException(nameof(failure)) : Chain(failure).OfType<DataverseRequestException>().ToArray();

    private static IEnumerable<Exception> Chain(Exception failure) => DirectCauses(failure).SelectMany(Chain).Prepend(failure);

    private static IEnumerable<Exception> DirectCauses(Exception failure) => failure is AggregateException aggregate ? aggregate.InnerExceptions : failure.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
}