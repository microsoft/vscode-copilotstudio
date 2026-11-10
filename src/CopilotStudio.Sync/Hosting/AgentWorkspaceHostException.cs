// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

/// <summary>The stable codes an <see cref="AgentWorkspaceHostException"/> carries.</summary>
public static class AgentWorkspaceHostErrorCodes
{
    /// <summary>The snapshot breaks a layout or declaration rule, listed in the exception's diagnostics.</summary>
    public const string SnapshotInvalid = "SnapshotInvalid";

    /// <summary>The target names no tenant, so the environment cannot be reached on the caller's behalf.</summary>
    public const string TargetInvalid = "TargetInvalid";

    /// <summary>The operation asked for a token or sent a request outside the single Dataverse organization it may use.</summary>
    public const string UnexpectedAudience = "UnexpectedAudience";

    /// <summary>The bot does not exist in the environment, or another bot carries its schema name.</summary>
    public const string BotNotFound = "BotNotFound";

    /// <summary>The workflow does not exist in the environment.</summary>
    public const string WorkflowNotFound = "WorkflowNotFound";

    /// <summary>The environment refused or failed part of the push.</summary>
    public const string PushFailed = "PushFailed";

    /// <summary>The environment refused or failed part of the clone.</summary>
    public const string CloneFailed = "CloneFailed";

    /// <summary>The environment refused or failed a lookup.</summary>
    public const string LookupFailed = "LookupFailed";

    /// <summary>The environment refused to publish the bot, or Copilot Studio finished the publish with another status than Succeeded.</summary>
    public const string PublishFailed = "PublishFailed";
}

/// <summary>Raised when a workspace host operation cannot be carried out, naming why with a stable code.</summary>
public sealed class AgentWorkspaceHostException : Exception
{
    /// <summary>Creates an exception with no detail.</summary>
    public AgentWorkspaceHostException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public AgentWorkspaceHostException(string message)
        : base(DataverseFailureText.Redact(message))
    {
    }

    /// <summary>Creates an exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure that caused it.</param>
    public AgentWorkspaceHostException(string message, Exception innerException)
        : base(DataverseFailureText.Redact(message), innerException)
    {
    }

    /// <summary>Creates an exception with a stable code, a message, the findings behind it and the failure that caused it.</summary>
    /// <param name="code">The stable code from <see cref="AgentWorkspaceHostErrorCodes"/>.</param>
    /// <param name="message">The message.</param>
    /// <param name="diagnostics">The findings behind the failure.</param>
    /// <param name="innerException">The failure that caused it, when there is one.</param>
    public AgentWorkspaceHostException(string code, string message, IReadOnlyList<AgentSyncDiagnostic>? diagnostics, Exception? innerException = null)
        : base(DataverseFailureText.Redact(message), innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Diagnostics = diagnostics ?? Array.Empty<AgentSyncDiagnostic>();
    }

    /// <summary>The stable code from <see cref="AgentWorkspaceHostErrorCodes"/>.</summary>
    public string Code { get; } = AgentWorkspaceHostErrorCodes.PushFailed;

    /// <summary>The findings behind the failure.</summary>
    public IReadOnlyList<AgentSyncDiagnostic> Diagnostics { get; } = Array.Empty<AgentSyncDiagnostic>();

    internal static AgentWorkspaceHostException BotNotFound(AgentSyncTarget target, Exception? innerException) => new(AgentWorkspaceHostErrorCodes.BotNotFound, $"The bot '{target.BotId ?? target.SchemaName}' does not exist in {target.EnvironmentId}.", null, innerException);
}
