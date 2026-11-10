// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Hosting;

/// <summary>The stable codes an <see cref="AgentSyncDiagnostic"/> carries.</summary>
public static class AgentSyncDiagnosticCodes
{
    /// <summary>The snapshot holds no file.</summary>
    public const string EmptySnapshot = "EmptySnapshot";

    /// <summary>A folder is not a single top-level segment.</summary>
    public const string FolderInvalid = "FolderInvalid";

    /// <summary>A path is absolute.</summary>
    public const string AbsolutePath = "AbsolutePath";

    /// <summary>A path holds a backslash.</summary>
    public const string BackslashInPath = "BackslashInPath";

    /// <summary>A path holds an empty, '.' or '..' segment.</summary>
    public const string RelativeSegmentInPath = "RelativeSegmentInPath";

    /// <summary>A path holds a '.mcs' segment, which only a session's sync state may use.</summary>
    public const string SyncStateInPath = "SyncStateInPath";

    /// <summary>Two paths differ only in case.</summary>
    public const string CaseOnlyDuplicate = "CaseOnlyDuplicate";

    /// <summary>A file lies outside the snapshot's folder and the collection folders it declares.</summary>
    public const string OutsideDeclaredFolders = "OutsideDeclaredFolders";

    /// <summary>A folder or path segment is a Windows device name such as CON, NUL or COM1, with or without an extension.</summary>
    public const string ReservedDeviceName = "ReservedDeviceName";

    /// <summary>A folder or path segment holds surrounding whitespace, a control character or one of the characters <c>:*?"&lt;&gt;|</c>, or a path segment ends with a dot.</summary>
    public const string UnsafePath = "UnsafePath";

    /// <summary>A folder is named more than once among an agent's folder and the collection folders it declares, ignoring case.</summary>
    public const string DuplicateCollection = "DuplicateCollection";

    /// <summary>The agent folder holds no settings.mcs.yml.</summary>
    public const string AgentProjectMissing = "AgentProjectMissing";

    /// <summary>A component collection is declared anywhere but a sibling top-level folder.</summary>
    public const string CollectionDeclarationInvalid = "CollectionDeclarationInvalid";

    /// <summary>A declared collection folder is not in the snapshot.</summary>
    public const string CollectionFolderMissing = "CollectionFolderMissing";

    /// <summary>A declared collection folder holds no collection.mcs.yml.</summary>
    public const string CollectionProjectMissing = "CollectionProjectMissing";

    /// <summary>A declared collection folder holds an agent.mcs.yml.</summary>
    public const string CollectionHoldsAgent = "CollectionHoldsAgent";

    /// <summary>A declared collection folder's collection.mcs.yml cannot be read or names no schema name.</summary>
    public const string CollectionSchemaNameMissing = "CollectionSchemaNameMissing";

    /// <summary>A declared collection folder's collection.mcs.yml names the schema name an earlier declared folder's does, ignoring case.</summary>
    public const string CollectionSchemaNameRepeated = "CollectionSchemaNameRepeated";

    /// <summary>A cli-copilot agent declares a component collection.</summary>
    public const string CliAgentDeclaresCollection = "CliAgentDeclaresCollection";

    /// <summary>The snapshot's authoring mode differs from the target's.</summary>
    public const string AuthoringShapeMismatch = "AuthoringShapeMismatch";

    /// <summary>The target's display name or the one the snapshot declares, which a push that may create the bot would give it, holds more than <see cref="AgentWorkspaceIdentity.MaximumDisplayNameLength"/> characters, which Copilot Studio refuses.</summary>
    public const string DisplayNameTooLong = "DisplayNameTooLong";

    /// <summary>The workflow folder does not hold exactly one workflow, or holds an agent.</summary>
    public const string WorkflowProjectInvalid = "WorkflowProjectInvalid";

    /// <summary>The workflow folder declares another workflow than the target names.</summary>
    public const string WorkflowIdMismatch = "WorkflowIdMismatch";

    /// <summary>The bot was pushed but the environment refused to publish it, Copilot Studio finished the publish with another status than Succeeded, or the publish's progress could not be read.</summary>
    public const string PublishFailed = AgentWorkspaceHostErrorCodes.PublishFailed;

    /// <summary>The bot was pushed, but Copilot Studio did not finish its publish within the push's publish timeout, or was still provisioning the bot when that timeout passed, so no publish was asked for.</summary>
    public const string PublishTimedOut = "PublishTimedOut";

    /// <summary>The target's unpublished designer draft of a workflow was overwritten.</summary>
    public const string UnpublishedDraftOverwritten = "UnpublishedDraftOverwritten";

    /// <summary>The environment refused to activate a workflow.</summary>
    public const string ActivationRefused = "ActivationRefused";

    /// <summary>The environment refused to store a custom connector.</summary>
    public const string ConnectorRefused = WorkspacePushDiagnosticCodes.ConnectorRefused;

    /// <summary>The environment refused to store a workflow.</summary>
    public const string WorkflowRefused = WorkspacePushDiagnosticCodes.WorkflowRefused;

    /// <summary>The environment refused to store an AI prompt.</summary>
    public const string PromptRefused = WorkspacePushDiagnosticCodes.PromptRefused;

    /// <summary>The target names no tenant, so the environment cannot be reached on the caller's behalf.</summary>
    public const string TenantMissing = "TenantMissing";

    /// <summary>The push failed unexpectedly at the stage, from <see cref="AgentPushStages"/>, that the diagnostic's subject names.</summary>
    public const string PushStageFailed = "PushStageFailed";

    /// <summary>Copilot Studio still reported a bot it provisions on its own as provisioning when the push's provisioning timeout passed, so nothing was pushed into the bot, which is kept for a later push.</summary>
    public const string AgentNotProvisioned = WorkspacePushErrorCodes.AgentNotProvisioned;
}
