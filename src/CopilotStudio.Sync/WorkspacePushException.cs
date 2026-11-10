// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The stable codes a <see cref="WorkspacePushException"/> carries.</summary>
public static class WorkspacePushErrorCodes
{
    /// <summary>The push stopped for a reason no other code names.</summary>
    public const string PushFailed = "PushFailed";

    /// <summary>A component references a connected agent that the target does not hold and the push could not create.</summary>
    public const string ConnectedAgentMissing = "ConnectedAgentMissing";

    /// <summary>The agent changed in the cloud while it was edited, and the push was not asked to overwrite it or still conflicted after re-reading it.</summary>
    public const string RemoteChanged = "RemoteChanged";

    /// <summary>The service's reply to the component push could not be read on any attempt.</summary>
    public const string ResponseUnreadable = "ResponseUnreadable";

    /// <summary>A connection reference the components use is missing from the target even after the cloud copy was re-read.</summary>
    public const string ConnectionReferenceMissing = "ConnectionReferenceMissing";

    /// <summary>The service refused to create a connection reference the target already holds, even after the cloud copy was re-read.</summary>
    public const string ConnectionReferenceDuplicate = "ConnectionReferenceDuplicate";

    /// <summary>The components reference something the target does not hold after the service refused workflows or AI prompts earlier in the push.</summary>
    public const string MissingAfterRefusals = "MissingAfterRefusals";

    /// <summary>A workflow tool names no workflow id or an empty one.</summary>
    public const string WorkflowToolIdEmpty = "WorkflowToolIdEmpty";

    /// <summary>The components were pushed but the knowledge files could not be uploaded.</summary>
    public const string KnowledgeUploadFailed = "KnowledgeUploadFailed";

    /// <summary>Copilot Studio still reported a bot it provisions on its own as provisioning when the wait for it ended, so nothing was pushed into it.</summary>
    public const string AgentNotProvisioned = "AgentNotProvisioned";
}

/// <summary>Raised when a workspace push stops, naming why with a stable code.</summary>
public sealed class WorkspacePushException : InvalidOperationException
{
    /// <summary>Creates an exception with no detail.</summary>
    public WorkspacePushException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public WorkspacePushException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure that caused it.</param>
    public WorkspacePushException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with a stable code, a message and the failure that caused it.</summary>
    /// <param name="code">The stable code from <see cref="WorkspacePushErrorCodes"/>.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure that caused it, when there is one.</param>
    public WorkspacePushException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
    }

    /// <summary>The stable code from <see cref="WorkspacePushErrorCodes"/>.</summary>
    public string Code { get; } = WorkspacePushErrorCodes.PushFailed;

    /// <summary>How many times the components were sent before the push gave up.</summary>
    public int Attempts { get; init; }

    /// <summary>The workflow tools that name no workflow id or an empty one.</summary>
    public IReadOnlyList<string> WorkflowTools { get; init; } = Array.Empty<string>();

    /// <summary>How many component changes the workspace held when the knowledge upload failed.</summary>
    public int PushedChangeCount { get; init; }

    /// <summary>The workflows and AI prompts the service refused or left disabled earlier in the push.</summary>
    public IReadOnlyList<WorkspacePushDiagnostic> Refusals { get; init; } = Array.Empty<WorkspacePushDiagnostic>();
}