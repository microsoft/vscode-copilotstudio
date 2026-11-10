// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class AgentWorkspaceHost : IAgentWorkspaceHost
{
    private readonly AgentWorkspaceCallFactory _calls;

    public AgentWorkspaceHost(AgentWorkspaceCallFactory calls) => _calls = calls ?? throw new ArgumentNullException(nameof(calls));

    public Task<AgentWorkspaceValidation> ValidateAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(AgentSnapshotValidator.ValidateAgent(snapshot));
    }

    public Task<AgentWorkspaceIdentity?> ReadIdentityAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadable(snapshot, AgentSnapshotValidator.ValidateAgent(snapshot).Diagnostics);

        return Task.FromResult(IdentityOf(snapshot));
    }

    public Task<WorkflowWorkspaceIdentity?> ReadWorkflowIdentityAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadable(snapshot, AgentSnapshotValidator.ValidateWorkflowProject(snapshot).Diagnostics);

        return Task.FromResult(ReadWorkflowIdentity(WorkspaceSnapshotFiles.Open(snapshot, snapshot.Folder)));
    }

    public async Task<string?> FindAgentAsync(AgentSyncTarget target, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        var schemaName = RequireSchemaName(target);
        cancellationToken.ThrowIfCancellationRequested();

        var botId = await RunInCallAsync(target.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.LookupFailed, call => call.Dataverse.GetAgentIdBySchemaNameAsync(schemaName, cancellationToken), cancellationToken).ConfigureAwait(false);

        return botId == Guid.Empty ? null : botId.ToString();
    }

    public async Task<string?> FindWorkflowAsync(WorkflowSyncTarget target, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        var workflowId = RequireWorkflowTarget(target) ?? throw new ArgumentException("The workflow has to be named by its identifier.", nameof(target));
        cancellationToken.ThrowIfCancellationRequested();

        var workflow = await RunInCallAsync(target.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.LookupFailed, call => call.WorkflowDataverse.GetWorkflowAsync(workflowId, cancellationToken), cancellationToken).ConfigureAwait(false);

        return workflow is null ? null : workflowId.ToString();
    }

    public async Task<AgentChangeProbe> ProbeAsync(AgentSyncTarget target, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        var botId = RequireTarget(target);
        cancellationToken.ThrowIfCancellationRequested();

        return await RunInCallAsync(target.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.LookupFailed, async call => await call.ChangeProbe.ProbeAgentAsync(botId, target.SchemaName, cancellationToken).ConfigureAwait(false) ?? throw AgentWorkspaceHostException.BotNotFound(target, null), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCloneResult> CloneAsync(AgentCloneRequest request, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        var botId = RequireCloneRequest(request);

        return await RunInCallAsync(request.Source.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.CloneFailed, call => new AgentClonePipeline(call).RunAsync(request, botId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentPushResult> PushAsync(AgentWorkspaceSnapshot snapshot, AgentSyncTarget target, AgentPushOptions options, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        var botId = RequireTarget(target);
        var validation = AgentSnapshotValidator.ValidateAgent(snapshot);

        if (validation.Diagnostics.Concat(AuthoringShapeMismatch(validation, target, snapshot.Folder)).Concat(DisplayNamesTooLongToCreate(snapshot, validation, target, botId is null && options is { CreateIfMissing: true })).ToArray() is { Length: > 0 } problems)
        {
            throw new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.SnapshotInvalid, $"The snapshot of '{snapshot.Folder}' cannot be pushed.", problems);
        }

        RequireOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        return await RunInCallAsync(target.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.PushFailed, call => new AgentPushPipeline(call).RunAsync(snapshot, target, botId, options, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowWorkspaceValidation> ValidateWorkflowAsync(AgentWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(AgentSnapshotValidator.ValidateWorkflowProject(snapshot));
    }

    public async Task<WorkflowCloneResult> CloneWorkflowAsync(WorkflowCloneRequest request, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var workflowId = RequireWorkflowTarget(request.Source) ?? throw new ArgumentException("A workflow id is required to clone a workflow.", nameof(request));
        AgentSnapshotValidator.RequireFolder(request.Folder, nameof(request));

        return await RunInCallAsync(request.Source.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.CloneFailed, call => new WorkflowSyncPipeline(call).CloneAsync(request, workflowId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowPushResult> PushWorkflowAsync(AgentWorkspaceSnapshot snapshot, WorkflowSyncTarget target, AgentPushOptions options, ISyncAuthProvider authentication, CancellationToken cancellationToken)
    {
        var workflowId = RequireWorkflowTarget(target);
        var validation = AgentSnapshotValidator.ValidateWorkflowProject(snapshot);

        if (validation.Workflow is not { } workflow || !validation.IsValid)
        {
            throw new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.SnapshotInvalid, $"The snapshot of '{snapshot.Folder}' cannot be pushed.", validation.Diagnostics);
        }

        if (workflowId is { } expected && expected != workflow.WorkflowId)
        {
            throw new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.SnapshotInvalid, $"The workflow folder '{snapshot.Folder}' holds workflow {workflow.WorkflowId}, not {expected}.", new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.WorkflowIdMismatch, $"The workflow folder declares {workflow.WorkflowId}, but the target names {expected}.", snapshot.Folder + WorkspaceSnapshotFiles.Separator + workflow.MetadataPath) });
        }

        RequireOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        return await RunInCallAsync(target.DataverseEndpoint, authentication, AgentWorkspaceHostErrorCodes.PushFailed, call => new WorkflowSyncPipeline(call).PushAsync(snapshot, target, workflow, options, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static Guid? RequireCloneRequest(AgentCloneRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var botId = RequireTarget(request.Source);

        if (AgentSnapshotLayout.FolderSetProblems(request.Folder, request.CollectionFolders?.Values ?? Array.Empty<string>()).FirstOrDefault() is { } problem)
        {
            throw new ArgumentException(problem.Message, nameof(request));
        }

        return botId;
    }

    private static Guid? RequireTarget(AgentSyncTarget target)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        RequireTenant(target.TenantId);

        if (string.IsNullOrWhiteSpace(target.SchemaName) && string.IsNullOrWhiteSpace(target.BotId))
        {
            throw new ArgumentException("The bot has to be named by its schema name or identifier.", nameof(target));
        }

        return ParseId(target.BotId, nameof(target));
    }

    private static string RequireSchemaName(AgentSyncTarget target)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        RequireTenant(target.TenantId);

        return string.IsNullOrWhiteSpace(target.SchemaName) ? throw new ArgumentException("The bot has to be named by its schema name.", nameof(target)) : target.SchemaName;
    }

    private static Guid? RequireWorkflowTarget(WorkflowSyncTarget target)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        RequireTenant(target.TenantId);

        return ParseId(target.WorkflowId, nameof(target));
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.TargetInvalid, "The target names no tenant.", new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.TenantMissing, "The caller's tenant is required to reach the environment on the caller's behalf.") });
        }
    }

    private static void RequireOptions(AgentPushOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.PublishTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.PublishTimeout, "The publish timeout must be positive.");
        }

        AgentProvisioning.RequirePositive(options.ProvisioningTimeout, nameof(options));
    }

    private static Guid? ParseId(string? identifier, string parameterName) => string.IsNullOrWhiteSpace(identifier) ? null : Guid.TryParse(identifier, out var parsed) && parsed != Guid.Empty ? parsed : throw new ArgumentException($"'{identifier}' is not an identifier.", parameterName);

    private static IEnumerable<AgentSyncDiagnostic> AuthoringShapeMismatch(AgentWorkspaceValidation validation, AgentSyncTarget target, string folder)
    {
        if (string.IsNullOrWhiteSpace(target.AuthoringShape) || validation.AuthoringMode is null)
        {
            return Array.Empty<AgentSyncDiagnostic>();
        }

        if (!AgentAuthoringModes.TryParse(target.AuthoringShape, out var targetType))
        {
            throw new ArgumentException($"'{target.AuthoringShape}' is not an authoring shape.", nameof(target));
        }

        return AgentAuthoringModes.TryParse(validation.AuthoringMode, out var snapshotType) && snapshotType == targetType ? Array.Empty<AgentSyncDiagnostic>() : new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.AuthoringShapeMismatch, $"The snapshot holds a {validation.AuthoringMode} agent, but the target bot is {target.AuthoringShape}.", AgentSnapshotLayout.SettingsPathOf(folder)) };
    }

    private static IEnumerable<AgentSyncDiagnostic> DisplayNamesTooLongToCreate(AgentWorkspaceSnapshot snapshot, AgentWorkspaceValidation validation, AgentSyncTarget target, bool createsTheBot)
    {
        if (!createsTheBot || !validation.IsValid)
        {
            yield break;
        }

        var declaredDisplayName = IdentityOf(snapshot)?.DeclaredDisplayName;

        if (AgentWorkspaceIdentity.DescribeExcessLength(declaredDisplayName) is { } declaredReason)
        {
            yield return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, declaredReason, AgentSnapshotLayout.SettingsPathOf(snapshot.Folder));
        }

        if (!string.Equals(target.DisplayName, declaredDisplayName, StringComparison.Ordinal) && AgentWorkspaceIdentity.DescribeExcessLength(target.DisplayName) is { } targetReason)
        {
            yield return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, targetReason);
        }
    }

    private static AgentWorkspaceIdentity? IdentityOf(AgentWorkspaceSnapshot snapshot) => AgentWorkspaceIdentity.TryRead(WorkspaceSnapshotFiles.Open(snapshot, snapshot.Folder));

    private static bool IsOperationFailure(Exception failure, CancellationToken cancellationToken) => failure is not AgentWorkspaceHostException && (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested);

    private static void RequireReadable(AgentWorkspaceSnapshot snapshot, IReadOnlyList<AgentSyncDiagnostic> problems)
    {
        if (problems.Count > 0)
        {
            throw new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.SnapshotInvalid, $"The snapshot of '{snapshot.Folder}' cannot be read.", problems);
        }
    }

    private static WorkflowWorkspaceIdentity? ReadWorkflowIdentity(IFileAccessor workspace)
    {
        var workflow = WorkflowWorkspace.FindAll(workspace)[0];

        return WorkflowMetadataFile.TryRead(workspace, workflow.MetadataPath) is { } metadata && metadata.WorkflowId == workflow.WorkflowId && metadata.Name is { } name && !string.IsNullOrWhiteSpace(name) ? new WorkflowWorkspaceIdentity(name) : null;
    }

    private static AgentWorkspaceHostException Translate(Exception failure, string fallbackCode, string stage)
    {
        for (var inner = failure.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is AgentWorkspaceHostException hostFailure)
            {
                return new AgentWorkspaceHostException(hostFailure.Code, hostFailure.Message, hostFailure.Diagnostics, failure);
            }
        }

        return failure switch
        {
            WorkspaceAttachException { Failure: WorkspaceAttachFailure.AgentNotFound or WorkspaceAttachFailure.AgentAlreadyExists } => new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.BotNotFound, DataverseFailureText.Of(failure), null, failure),
            StandaloneWorkflowException { Failure: StandaloneWorkflowFailure.SourceNotFound } => new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.WorkflowNotFound, DataverseFailureText.Of(failure), null, failure),
            _ when fallbackCode == AgentWorkspaceHostErrorCodes.PushFailed && NotProvisioned(failure) is { } notProvisioned => new AgentWorkspaceHostException(fallbackCode, notProvisioned.Message, new[] { AgentPushStages.Failure(stage), new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.AgentNotProvisioned, notProvisioned.Message) { Subject = (failure as WorkspaceAttachException)?.AgentId?.ToString() } }, failure),
            _ => new AgentWorkspaceHostException(fallbackCode, DataverseFailureText.Of(failure), fallbackCode == AgentWorkspaceHostErrorCodes.PushFailed ? new[] { AgentPushStages.Failure(stage) } : null, failure),
        };
    }

    private static WorkspacePushException? NotProvisioned(Exception failure) => failure is WorkspacePushException { Code: WorkspacePushErrorCodes.AgentNotProvisioned } notProvisioned ? notProvisioned : failure.InnerException is { } inner ? NotProvisioned(inner) : null;

    private async Task<TResult> RunInCallAsync<TResult>(Uri organization, ISyncAuthProvider authentication, string failureCode, Func<AgentWorkspaceCall, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        var call = _calls.Open(organization, authentication);

        try
        {
            return await operation(call).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsOperationFailure(failure, cancellationToken))
        {
            throw Translate(failure, failureCode, call.Stage);
        }
        finally
        {
            await call.DisposeAsync().ConfigureAwait(false);
        }
    }
}
