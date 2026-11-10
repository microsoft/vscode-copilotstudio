// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>What went wrong while writing an agent scaffold.</summary>
public enum AgentScaffoldFailure
{
    /// <summary>The requested template does not exist.</summary>
    TemplateNotFound = 2,

    /// <summary>The display name holds no letter or digit, or holds a control character.</summary>
    DisplayNameUnusable = 6,

    /// <summary>The display name is longer than the service accepts.</summary>
    DisplayNameTooLong = 10,
}

/// <summary>An agent scaffold could not be written.</summary>
public sealed class AgentScaffoldException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="subject">The value that was refused.</param>
    /// <param name="message">The message describing the failure.</param>
    public AgentScaffoldException(AgentScaffoldFailure failure, string? subject, string message)
        : base(message)
    {
        Failure = failure;
        Subject = subject;
    }

    /// <summary>Creates the exception.</summary>
    public AgentScaffoldException() => Failure = AgentScaffoldFailure.TemplateNotFound;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    public AgentScaffoldException(string message)
        : base(message) => Failure = AgentScaffoldFailure.TemplateNotFound;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public AgentScaffoldException(string message, Exception innerException)
        : base(message, innerException) => Failure = AgentScaffoldFailure.TemplateNotFound;

    /// <summary>What went wrong.</summary>
    public AgentScaffoldFailure Failure { get; }

    /// <summary>The value that was refused, such as a template or display name.</summary>
    public string? Subject { get; }

    internal static AgentScaffoldException TemplateNotFound(string? template) =>
        new(AgentScaffoldFailure.TemplateNotFound, template, $"The template '{template}' does not exist.");
}
