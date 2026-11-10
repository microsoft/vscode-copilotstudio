// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>What went wrong while reading or provisioning the connection references a workspace declares.</summary>
public enum DeclaredConnectionReferenceFailure
{
    /// <summary>A declaration file could not be read or does not hold valid declarations.</summary>
    Unreadable = 0,

    /// <summary>One logical name is declared for two different connectors.</summary>
    Conflict = 1,

    /// <summary>The target did not confirm a declared reference as the workspace declares it.</summary>
    NotConfirmed = 2,
}

/// <summary>The connection references a workspace declares could not be read or provisioned.</summary>
public sealed class DeclaredConnectionReferenceException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The failure this one wraps, when there is one.</param>
    public DeclaredConnectionReferenceException(DeclaredConnectionReferenceFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    /// <summary>Creates the exception.</summary>
    public DeclaredConnectionReferenceException() => Failure = DeclaredConnectionReferenceFailure.Unreadable;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    public DeclaredConnectionReferenceException(string message)
        : base(message) => Failure = DeclaredConnectionReferenceFailure.Unreadable;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public DeclaredConnectionReferenceException(string message, Exception innerException)
        : base(message, innerException) => Failure = DeclaredConnectionReferenceFailure.Unreadable;

    /// <summary>What went wrong.</summary>
    public DeclaredConnectionReferenceFailure Failure { get; }

    /// <summary>The workspace-relative declaration file that could not be read, or that declared the second of two connectors.</summary>
    public string? Path { get; private init; }

    /// <summary>Why the declaration file could not be read.</summary>
    public string? Reason { get; private init; }

    /// <summary>The logical name declared for two connectors.</summary>
    public string? LogicalName { get; private init; }

    /// <summary>The workspace-relative declaration file that declared the first of two connectors.</summary>
    public string? FirstPath { get; private init; }

    /// <summary>The connector the first declaration names.</summary>
    public string? FirstConnectorId { get; private init; }

    /// <summary>The connector the second declaration names.</summary>
    public string? SecondConnectorId { get; private init; }

    internal static DeclaredConnectionReferenceException Unreadable(string path, string reason, Exception? innerException = null) =>
        new(DeclaredConnectionReferenceFailure.Unreadable, $"The connection references declared at '{path}' could not be read: {reason}", innerException)
        {
            Path = path,
            Reason = reason,
        };

    internal static DeclaredConnectionReferenceException Conflict(string logicalName, string firstPath, string firstConnectorId, string secondPath, string secondConnectorId) =>
        new(DeclaredConnectionReferenceFailure.Conflict, $"Connection reference '{logicalName}' is declared for two connectors: '{firstConnectorId}' at '{firstPath}' and '{secondConnectorId}' at '{secondPath}'.")
        {
            LogicalName = logicalName,
            FirstPath = firstPath,
            FirstConnectorId = firstConnectorId,
            Path = secondPath,
            SecondConnectorId = secondConnectorId,
        };

    internal static DeclaredConnectionReferenceException NotConfirmed(IReadOnlyList<string> logicalNames) =>
        new(DeclaredConnectionReferenceFailure.NotConfirmed, $"The target did not confirm these connection references as the workspace declares them: {string.Join(", ", logicalNames)}. Nothing was recorded as deployed.");
}
