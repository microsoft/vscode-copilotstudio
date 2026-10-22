// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>What went wrong while reading or writing a standalone workflow.</summary>
public enum StandaloneWorkflowFailure
{
    /// <summary>A directory holds no workflow.</summary>
    NotFound = 0,

    /// <summary>A directory holds more than one workflow.</summary>
    Ambiguous = 1,

    /// <summary>A workflow's metadata file cannot be read.</summary>
    MetadataUnreadable = 2,

    /// <summary>The workflow a clone names does not exist in its environment.</summary>
    SourceNotFound = 3,

    /// <summary>The service refused the workflow the push sent.</summary>
    PushFailed = 4,

    /// <summary>The environment refused the workflow because a connection reference it uses has no usable connection.</summary>
    ConnectionsNotReady = 5,

    /// <summary>A replacement deleted the cloud copy and could not write it back.</summary>
    ReplaceLostTheCloudCopy = 6,

    /// <summary>The directory a clone would write to already holds another workflow or an agent.</summary>
    ProjectFolderOccupied = 7,

    /// <summary>The project a clone would write over holds a workflow edit the service has not seen.</summary>
    ProjectHoldsUnpushedChanges = 8,

    /// <summary>The workspace a download would write over holds a workflow edit the service has not seen.</summary>
    WorkspaceHoldsUnpushedChanges = 9,

    /// <summary>A workflow name is one Dataverse would refuse or no folder name could carry.</summary>
    InvalidName = 10,

    /// <summary>The workflow is managed.</summary>
    Managed = 11,

    EnvironmentHoldsUnpulledChanges = 12,

    EnvironmentHoldsUnpublishedDraft = 13,
}

/// <summary>A standalone workflow could not be read or written.</summary>
public sealed class StandaloneWorkflowException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="message">The message describing the failure.</param>
    public StandaloneWorkflowException(StandaloneWorkflowFailure failure, string message)
        : base(message) => Failure = failure;

    /// <summary>Creates the exception.</summary>
    public StandaloneWorkflowException() => Failure = StandaloneWorkflowFailure.NotFound;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    public StandaloneWorkflowException(string message)
        : base(message) => Failure = StandaloneWorkflowFailure.NotFound;

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The failure this one wraps.</param>
    public StandaloneWorkflowException(string message, Exception innerException)
        : base(message, innerException) => Failure = StandaloneWorkflowFailure.NotFound;

    /// <summary>What went wrong.</summary>
    public StandaloneWorkflowFailure Failure { get; }

    /// <summary>The subject of the failure, such as a workflow name or identifier.</summary>
    public string? Subject { get; private init; }

    /// <summary>The environment the failure happened in, when it happened against one.</summary>
    public string? EnvironmentId { get; private init; }

    /// <summary>The connection reference the environment refused, when it named one.</summary>
    public string? ConnectionReference { get; private init; }

    /// <summary>The reason the service reported, when it reported one.</summary>
    public string? Reason { get; private init; }

    /// <summary>The workflow folders a directory holds, when more than one was found.</summary>
    public IReadOnlyList<string> FolderNames { get; private init; } = Array.Empty<string>();

    /// <summary>Why restoring the deleted cloud copy failed, when a replacement lost it.</summary>
    public string? RestoreFailure { get; private init; }

    internal static StandaloneWorkflowException NotFound(string workspaceRoot) =>
        new(StandaloneWorkflowFailure.NotFound, $"{workspaceRoot} holds no workflow. A workflow lives in workflows/<name>-<id>/ and carries both workflow.json and metadata.yml.")
        {
            Subject = workspaceRoot,
        };

    internal static StandaloneWorkflowException Ambiguous(IEnumerable<string> folderNames)
    {
        var names = folderNames as IReadOnlyList<string> ?? folderNames.ToArray();

        return new StandaloneWorkflowException(StandaloneWorkflowFailure.Ambiguous, $"This directory holds more than one workflow: {string.Join(", ", names)}.")
        {
            FolderNames = names,
        };
    }

    internal static StandaloneWorkflowException MetadataUnreadable(string metadataPath, string reason) =>
        new(StandaloneWorkflowFailure.MetadataUnreadable, $"{metadataPath} could not be read as workflow metadata: {reason}")
        {
            Subject = metadataPath,
            Reason = reason,
        };

    internal static StandaloneWorkflowException SourceNotFound(Guid workflowId, string? environmentId) =>
        new(StandaloneWorkflowFailure.SourceNotFound, $"No workflow with id {workflowId} exists in {environmentId}.")
        {
            Subject = workflowId.ToString(),
            EnvironmentId = environmentId,
        };

    internal static StandaloneWorkflowException Managed(Guid workflowId, string? workflowName, string? environmentId)
    {
        var subject = workflowName ?? workflowId.ToString();

        return new(StandaloneWorkflowFailure.Managed, string.Format(CultureInfo.InvariantCulture, "The workflow '{0}' ({1}) is managed{2}. A managed flow changes only through the solution that installed it, so it cannot be cloned or pulled.", subject, workflowId, InEnvironment(environmentId)))
        {
            Subject = subject,
            EnvironmentId = environmentId,
        };
    }

    internal static StandaloneWorkflowException UnusableProjectName(string workflowName) => InvalidName(workflowName, $"The workflow name '{workflowName}' holds no character a folder name may use.");

    internal static StandaloneWorkflowException InvalidName(string? workflowName, string reason) =>
        new(StandaloneWorkflowFailure.InvalidName, reason)
        {
            Subject = workflowName,
            Reason = reason,
        };

    internal static StandaloneWorkflowException InvalidMetadataName(string metadataPath, string reason) =>
        new(StandaloneWorkflowFailure.InvalidName, $"{metadataPath} declares a workflow name that cannot be used: {reason}")
        {
            Subject = metadataPath,
            Reason = reason,
        };

    internal static StandaloneWorkflowException ProjectFolderOccupied(string projectFolder) =>
        new(StandaloneWorkflowFailure.ProjectFolderOccupied, $"The folder '{projectFolder}' already holds another project. Clone this workflow somewhere else.")
        {
            Subject = projectFolder,
        };

    internal static StandaloneWorkflowException ProjectHoldsUnpushedChanges(string projectFolder) =>
        new(StandaloneWorkflowFailure.ProjectHoldsUnpushedChanges, $"The folder '{projectFolder}' already holds this workflow with changes the environment has not seen. Push or discard them, or clone this workflow somewhere else.")
        {
            Subject = projectFolder,
        };

    internal static StandaloneWorkflowException WorkspaceHoldsUnpushedChanges(string folderName) =>
        new(StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges, $"'{folderName}' has conflict local changes, and a pull would replace them.")
        {
            Subject = folderName,
        };

    internal static StandaloneWorkflowException EnvironmentHoldsUnpulledChanges(string folderName, string? environmentId) =>
        new(StandaloneWorkflowFailure.EnvironmentHoldsUnpulledChanges, $"'{folderName}' has conflict remote change, and applying changes would replace them. Use Get changes to get them, or Apply changes again and choose Overwrite remote changes to replace them.")
        {
            Subject = folderName,
            EnvironmentId = environmentId,
        };

    internal static StandaloneWorkflowException EnvironmentHoldsUnpublishedDraft(string workflowName, string? environmentId) =>
        new(StandaloneWorkflowFailure.EnvironmentHoldsUnpublishedDraft, $"'{workflowName}' has a draft version that was saved in Copilot Studio but not published, and the environment refuses to apply changes while it exists. Publish the draft in Copilot Studio, then use Get changes to get it, or Apply changes again and choose Overwrite remote changes to replace it.")
        {
            Subject = workflowName,
            EnvironmentId = environmentId,
        };

    internal static string InEnvironment(string? environmentId) => string.IsNullOrWhiteSpace(environmentId) ? string.Empty : $" in {environmentId}";

    internal static StandaloneWorkflowException PushFailed(string workflowName, string? environmentId, string reason) =>
        new(StandaloneWorkflowFailure.PushFailed, $"The workflow '{workflowName}' could not be written to {environmentId}: {reason}")
        {
            Subject = workflowName,
            EnvironmentId = environmentId,
            Reason = reason,
        };

    internal static StandaloneWorkflowException ConnectionsNotReady(string workflowName, string? environmentId, string connectionReference) =>
        new(StandaloneWorkflowFailure.ConnectionsNotReady, string.Format(CultureInfo.InvariantCulture, "The workflow '{0}' already exists in {1} and the environment will not accept a change to it while '{2}' has no usable connection.", workflowName, environmentId, connectionReference))
        {
            Subject = workflowName,
            EnvironmentId = environmentId,
            ConnectionReference = connectionReference,
        };

    internal static StandaloneWorkflowException ReplaceLostTheCloudCopy(string workflowName, string? environmentId, string writeFailure, string restoreFailure) =>
        new(StandaloneWorkflowFailure.ReplaceLostTheCloudCopy, string.Format(CultureInfo.InvariantCulture, "The workflow '{0}' no longer exists in {1}. The environment refused an update, so it was deleted and written again, but writing it back failed ({2}) and so did restoring the original ({3}).", workflowName, environmentId, writeFailure, restoreFailure))
        {
            Subject = workflowName,
            EnvironmentId = environmentId,
            Reason = writeFailure,
            RestoreFailure = restoreFailure,
        };
}
