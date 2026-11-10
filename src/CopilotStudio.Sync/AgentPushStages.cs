// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Hosting;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The stages of a push that a <see cref="AgentSyncDiagnosticCodes.PushStageFailed"/> diagnostic names as its subject.</summary>
public static class AgentPushStages
{
    /// <summary>Finding or creating the target bot and reading its cloud copy.</summary>
    public const string Agent = "agent";

    /// <summary>Pushing the custom connectors.</summary>
    public const string Connectors = "connectors";

    /// <summary>Creating the connection references the workspace declares.</summary>
    public const string ConnectionReferences = "connection references";

    /// <summary>Pushing the workflows.</summary>
    public const string Workflows = "workflows";

    /// <summary>Pushing the AI prompts.</summary>
    public const string Prompts = "prompts";

    /// <summary>Reading, pushing and confirming the topics, actions, knowledge and other components, and the component collections that hold them.</summary>
    public const string Components = "components";

    /// <summary>Activating the workflows the push left as drafts once every connection they use is bound.</summary>
    public const string Activation = "activation";

    /// <summary>Publishing the bot.</summary>
    public const string Publish = "publish";

    internal static AgentSyncDiagnostic Failure(string stage) => new(AgentSyncDiagnosticCodes.PushStageFailed, $"The push stopped at the '{stage}' stage.") { Subject = stage };
}
