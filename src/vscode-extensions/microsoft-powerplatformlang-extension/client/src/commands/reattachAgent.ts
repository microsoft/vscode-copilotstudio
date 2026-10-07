import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { listEnvironmentsAsync } from '../clients/bapClient';
import { clearAuthAccountState, getPreferredTreeAccount, listStoredAccounts, switchAccount } from '../clients/account';
import { autoBindAgentConnections, promptManageConnectionsForWorkspaces } from '../connections/connectionManager';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { buildEnvironmentPickItems } from '../services/accountEnvPicker';
import { buildLspRequestPayload, lspClient } from '../services/lspClient';
import logger, { formatPii, PiiRedactionType, prepareLogData } from '../services/logger';
import { registerVirtualKnowledgeProvider } from '../knowledgeFiles/virtualKnowledgeFile';
import {
  CopilotStudioWorkspace,
  getAllWorkspaces,
  getWorkspaceKindLabel,
  hasConnectionFileInWorkspace,
  WorkspaceType,
} from '../sync/localWorkspaces';
import { pushNewWorkspace } from '../sync/workspaceScm';
import { selectWorkspace } from '../sync/workspacePicker';
import {
  getActiveSyncUri,
  getOrAddSynchronizer,
  logAIPromptIssues,
  withSyncCommandBusy,
} from '../sync/workspaceSynchronizer';
import {
  AccountInfo,
  EnvironmentInfo,
  FinalizeRetargetResponse,
  ReattachAgentRequest,
  ReattachAgentResponse,
  RetargetConflictResolution,
} from '../types';
import { ReattachPlan, buildReattachPlanCore } from './reattachPlan';
import { getDiagnosticsErrors } from './syncWorkspace';

const ACCOUNT_TRANSITION_TIMEOUT_MS = 120_000;

type ReattachEnvironmentPickItem = vscode.QuickPickItem & {
  environment: EnvironmentInfo;
  sourceAccount?: AccountInfo;
};

type ReattachAccountPickItem = vscode.QuickPickItem & {
  account: AccountInfo;
};

type ReattachWorkspaceResult = {
  workspace: CopilotStudioWorkspace;
  response: ReattachAgentResponse;
  wasRetarget: boolean;
};

type ReattachConnectionSummary = {
  boundConnectionCount: number;
  enabledWorkflowCount: number;
  workspacesNeedingConnections: CopilotStudioWorkspace[];
};

type ReattachOutcome =
  | {
      kind: 'success';
      message: string;
    }
  | {
      kind: 'cancelled';
      message: string;
    }
  | {
      kind: 'failed';
      error: unknown;
    };

type ReattachFailureLevel = 'warning' | 'error';

type ReattachFailureAction = {
  label: string;
  command: string;
};

type ReattachOutcomeInteraction = {
  selection: Thenable<string | undefined>;
  action: ReattachFailureAction;
};

type ReattachExecutionResult = {
  outcomeInteraction?: ReattachOutcomeInteraction;
  workspacesNeedingConnections?: CopilotStudioWorkspace[];
};

class ReattachError extends Error {
  constructor(
    message: string,
    readonly level: ReattachFailureLevel = 'error',
    readonly reason?: unknown,
    readonly action?: ReattachFailureAction
  ) {
    super(message);
    this.name = 'ReattachError';
  }
}

class ReattachCancelledError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ReattachCancelledError';
  }
}

const getWorkspaceFolderPath = (workspace: CopilotStudioWorkspace): string =>
  vscode.Uri.parse(workspace.workspaceUri).fsPath;

const buildReattachPlan = (workspace: CopilotStudioWorkspace): ReattachPlan => {
  const workspaceFolder = getWorkspaceFolderPath(workspace);
  const referencesFilePath = path.join(workspaceFolder, 'references.mcs.yml');
  const referencesContent =
    workspace.type === WorkspaceType.Agent && fs.existsSync(referencesFilePath)
      ? fs.readFileSync(referencesFilePath, 'utf-8')
      : undefined;
  const candidateCollections = getAllWorkspaces().map(candidate => ({
    workspace: candidate,
    folderPath: getWorkspaceFolderPath(candidate),
  }));
  return buildReattachPlanCore(workspace, workspaceFolder, referencesContent, candidateCollections);
};

const selectReattachWorkspace = async (
  treeItem?: { workspace?: CopilotStudioWorkspace }
): Promise<CopilotStudioWorkspace> => {
  const workspace =
    treeItem?.workspace ??
    (await selectWorkspace(candidate => candidate.type !== WorkspaceType.ComponentCollection));
  if (!workspace || workspace.type === WorkspaceType.ComponentCollection) {
    throw new ReattachCancelledError('Reattach agent canceled before a workspace was selected.');
  }
  return workspace;
};

const pickTargetEnvironment = async (
  isAttached: boolean
): Promise<ReattachEnvironmentPickItem> => {
  const quickPick = vscode.window.createQuickPick();
  quickPick.title = isAttached ? 'Select environment to retarget' : 'Select environment to reattach';
  quickPick.placeholder = 'Choose an environment';
  quickPick.ignoreFocusOut = true;
  quickPick.busy = true;
  quickPick.buttons = [
    { iconPath: new vscode.ThemeIcon('sign-in'), tooltip: 'Switch account' },
  ];

  let pickPhase: 'account' | 'environment' = 'environment';
  let settled = false;
  let accountTransitionInProgress = false;
  const runAccountTransition = async (action: () => Promise<void>): Promise<void> => {
    if (accountTransitionInProgress) {
      return;
    }

    accountTransitionInProgress = true;
    let timeout: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        action(),
        new Promise<never>((_, reject) => {
          timeout = setTimeout(
            () =>
              reject(
                new ReattachError(
                  `Account and environment selection did not complete within ${Math.ceil(
                    ACCOUNT_TRANSITION_TIMEOUT_MS / 60_000
                  )} minutes. Please try again.`
                )
              ),
            ACCOUNT_TRANSITION_TIMEOUT_MS
          );
        }),
      ]);
    } finally {
      if (timeout !== undefined) {
        clearTimeout(timeout);
      }
      accountTransitionInProgress = false;
      if (!settled) {
        quickPick.show();
      }
    }
  };

  const loadEnvironmentsForAccount = async (account: AccountInfo): Promise<void> => {
    const previousState = {
      phase: pickPhase,
      title: quickPick.title,
      placeholder: quickPick.placeholder,
      items: quickPick.items,
    };

    pickPhase = 'environment';
    quickPick.busy = true;
    quickPick.title = isAttached ? 'Select environment to retarget' : 'Select environment to reattach';
    quickPick.placeholder = 'Choose an environment';
    try {
      const environments = await listEnvironmentsAsync(
        DefaultCoreServicesClusterCategory,
        null,
        account.accountId ?? null,
        account.accountEmail,
        true
      );
      if (settled) {
        return;
      }
      quickPick.items = buildEnvironmentPickItems(environments, account);
    } catch (error) {
      if (settled) {
        return;
      }
      logger.logError(
        TelemetryEventsKeys.LoadEnvironmentError,
        `[Reattach] Failed to load environments for ${formatPii(
          account.accountEmail ?? account.accountId ?? 'Unknown',
          PiiRedactionType.EmailAddress
        )}`,
        { error }
      );
      pickPhase = previousState.phase;
      quickPick.title = previousState.title;
      quickPick.placeholder = previousState.placeholder;
      quickPick.items = previousState.items;
    } finally {
      if (!settled) {
        quickPick.busy = false;
      }
    }
  };

  const loadAccountsOrEnvironments = async (): Promise<void> => {
    pickPhase = 'environment';
    quickPick.busy = true;
    const accounts = (await listStoredAccounts()).map<AccountInfo>(account => ({
      accountId: account.accountId,
      accountEmail: account.accountEmail ?? '',
      tenantId: '',
    }));
    if (settled) {
      return;
    }

    if (accounts.length === 0) {
      quickPick.items = [];
      quickPick.busy = false;
      return;
    }

    if (accounts.length === 1) {
      await loadEnvironmentsForAccount(accounts[0]);
      return;
    }

    pickPhase = 'account';
    quickPick.title = 'Select account';
    quickPick.placeholder = 'Choose an account';
    quickPick.items = accounts.map<ReattachAccountPickItem>(account => ({
      label: account.accountEmail || account.accountId || 'Account',
      account,
    }));
    quickPick.busy = false;
  };

  return await new Promise<ReattachEnvironmentPickItem>((resolve, reject) => {
    const disposables: vscode.Disposable[] = [];
    const finish = (result?: ReattachEnvironmentPickItem, error?: unknown) => {
      if (settled) {
        return;
      }

      settled = true;
      disposables.forEach(disposable => disposable.dispose());
      quickPick.dispose();
      if (error !== undefined) {
        reject(error);
      } else if (result) {
        resolve(result);
      } else {
        reject(
          new ReattachCancelledError(
            `${isAttached ? 'Retarget' : 'Reattach'} agent canceled before an environment was selected.`
          )
        );
      }
    };

    disposables.push(
      quickPick.onDidHide(() => {
        if (!accountTransitionInProgress) {
          finish();
        }
      }),
      quickPick.onDidTriggerButton(button => {
        if (button.tooltip !== 'Switch account') {
          return;
        }
        void runAccountTransition(async () => {
            await switchAccount(DefaultCoreServicesClusterCategory);
            if (settled) {
              return;
            }
            await loadAccountsOrEnvironments();
          })
          .catch(error => {
            if (error instanceof ReattachError) {
              finish(undefined, error);
              return;
            }
            logger.logWarning(TelemetryEventsKeys.ReattachAgentWarning, 'Switching accounts failed.', { error });
          });
      }),
      quickPick.onDidAccept(() => {
        if (pickPhase === 'account') {
          const selectedAccount = quickPick.selectedItems[0] as ReattachAccountPickItem;
          if (selectedAccount?.account) {
            void runAccountTransition(() => loadEnvironmentsForAccount(selectedAccount.account))
              .catch(error => finish(undefined, error));
          }
          return;
        }

        const selectedEnvironment = quickPick.selectedItems[0] as ReattachEnvironmentPickItem;
        if (selectedEnvironment?.environment) {
          finish(selectedEnvironment);
        }
      })
    );

    void loadAccountsOrEnvironments()
      .then(() => {
        if (!settled) {
          quickPick.show();
        }
      })
      .catch(error => finish(undefined, error));
  });
};

const confirmReattach = async (
  workspace: CopilotStudioWorkspace,
  plan: ReattachPlan,
  targetEnvironmentName: string,
  isAttached: boolean
): Promise<void> => {
  const collectionCount = plan.workspaces.filter(
    candidate => candidate.type === WorkspaceType.ComponentCollection
  ).length;

  if (isAttached) {
    const retarget = 'Retarget';
    const subject =
      collectionCount === 0
        ? `this agent (${workspace.displayName})`
        : `this agent (${workspace.displayName}) and ${collectionCount} component collection${
            collectionCount === 1 ? '' : 's'
          }`;
    const collectionNote =
      collectionCount === 0
        ? ''
        : ` Existing component collections with the same name in '${targetEnvironmentName}' will be updated with your local content.`;
    const choice = await vscode.window.showWarningMessage(
      `Retarget ${subject} to '${targetEnvironmentName}'? Your local content will be uploaded to '${targetEnvironmentName}' and connected there.${collectionNote}`,
      { modal: true },
      retarget
    );
    if (choice !== retarget) {
      throw new ReattachCancelledError('Retarget agent canceled before confirmation.');
    }
    return;
  }

  if (collectionCount > 0) {
    const reattach = 'Reattach';
    const choice = await vscode.window.showWarningMessage(
      `Reattach this agent (${workspace.displayName}) and ${collectionCount} component collection${
        collectionCount === 1 ? '' : 's'
      } to '${targetEnvironmentName}'? Your local content will be uploaded, and existing component collections with the same name in '${targetEnvironmentName}' will be updated with your local content.`,
      { modal: true },
      reattach
    );
    if (choice !== reattach) {
      throw new ReattachCancelledError('Reattach agent canceled before confirmation.');
    }
  }
};

const validateDiagnostics = async (
  plan: ReattachPlan,
  isAttached: boolean
): Promise<void> => {
  if (!isAttached) {
    return;
  }

  for (const workspace of plan.workspaces) {
    const diagnostics = await getDiagnosticsErrors(workspace);
    if (diagnostics.count === 0) {
      continue;
    }

    const message = `Cannot retarget agent: found ${diagnostics.count} error(s) in ${
      diagnostics.files
    } file(s) for '${formatPii(
      workspace.displayName,
      PiiRedactionType.AgentName
    )}'. Fix the errors and try again.`;
    throw new ReattachError(message, 'warning', undefined, {
      label: 'View Details',
      command: 'workbench.actions.view.problems',
    });
  }
};

const runReattachForWorkspace = async (
  workspace: CopilotStudioWorkspace,
  basePayload: Omit<
    ReattachAgentRequest,
    'workspaceUri' | 'allowRetarget' | 'conflictResolution'
  >,
  targetEnvironmentName: string
): Promise<ReattachWorkspaceResult> => {
  const workspaceUri = workspace.workspaceUri;
  const wasRetarget = hasConnectionFileInWorkspace(workspaceUri);
  const sendReattach = async (
    resolution: RetargetConflictResolution
  ): Promise<ReattachAgentResponse> =>
    await lspClient.sendRequest<ReattachAgentResponse>(LspMethods.REATTACH_AGENT, {
      ...basePayload,
      workspaceUri,
      allowRetarget: wasRetarget,
      conflictResolution: resolution,
    });

  let response = await sendReattach(
    workspace.type === WorkspaceType.ComponentCollection
      ? RetargetConflictResolution.ReuseExisting
      : RetargetConflictResolution.Prompt
  );
  while (response.code === 200 && response.schemaConflict) {
    const reuseExisting = 'Reuse existing';
    const choice = await vscode.window.showWarningMessage(
      `A ${getWorkspaceKindLabel(workspace)} with the same schema name already exists in '${targetEnvironmentName}'. Reattach to the existing ${getWorkspaceKindLabel(
        workspace
      )} and update it with your local content?`,
      { modal: true },
      reuseExisting
    );
    if (choice !== reuseExisting) {
      throw new ReattachCancelledError(
        'Agent reattach canceled while resolving an existing agent conflict.'
      );
    }
    response = await sendReattach(RetargetConflictResolution.ReuseExisting);
  }

  if (response.code !== 200) {
    const message = `Reattach failed for '${formatPii(
      workspace.displayName,
      PiiRedactionType.AgentName
    )}'`;
    throw new ReattachError(
      message,
      'error',
      new Error(response.message ?? 'Unknown error')
    );
  }

  const reattachedWorkspace: CopilotStudioWorkspace = {
    ...workspace,
    syncInfo: response.agentSyncInfo,
  };

  return { workspace: reattachedWorkspace, response, wasRetarget };
};

const finalizeRetargets = async (
  results: ReattachWorkspaceResult[],
  pushSucceeded: boolean
): Promise<void> => {
  const retargets = results.filter(result => result.wasRetarget);
  const outcomes = await Promise.allSettled(
    retargets.map(result =>
      lspClient.sendRequest<FinalizeRetargetResponse>(LspMethods.FINALIZE_RETARGET, {
        workspaceUri: result.workspace.workspaceUri,
        pushSucceeded,
      })
    )
  );
  const failures = outcomes.filter(
    (outcome): outcome is PromiseRejectedResult => outcome.status === 'rejected'
  );
  if (failures.length > 0) {
    throw new Error(
      `Failed to finalize ${failures.length} of ${retargets.length} retarget operation(s): ${failures
        .map(failure => (failure.reason as Error).message)
        .join('; ')}`
    );
  }
};

const executeReattach = async (
  context: vscode.ExtensionContext,
  plan: ReattachPlan,
  environment: EnvironmentInfo,
  targetEnvironmentName: string,
  selectedAccount?: Partial<AccountInfo>,
): Promise<ReattachWorkspaceResult[]> => {
  const reattachedWorkspaceResults: ReattachWorkspaceResult[] = [];
  try {
    const basePayload = await buildLspRequestPayload(
      undefined,
      environment,
      selectedAccount,
      true
    );

    for (const workspaceToReattach of plan.workspaces) {
      const result = await runReattachForWorkspace(
        workspaceToReattach,
        basePayload,
        targetEnvironmentName
      );
      reattachedWorkspaceResults.push(result);
      if (result.response.requiresLocalPush) {
        await pushNewWorkspace(context, result.workspace, result.wasRetarget);
      }
    }

    try {
      await finalizeRetargets(reattachedWorkspaceResults, true);
    } catch (rollbackError) {
      logger.logWarning(
        TelemetryEventsKeys.ReattachAgentWarning,
        'Retarget succeeded but clearing the retarget backup failed; the workspaces remain on the new environment',
        { error: rollbackError }
      );
    }
  } catch (error) {
    if (reattachedWorkspaceResults.some(result => result.wasRetarget)) {
      try {
        await finalizeRetargets(reattachedWorkspaceResults, false);
      } catch (rollbackError) {
        throw new ReattachError(
          'Retarget failed and rollback to the previous environment failed',
          'error',
          rollbackError
        );
      }

      if (
        !(error instanceof ReattachCancelledError) &&
        !(error instanceof ReattachError)
      ) {
        throw new ReattachError(
          'Retargeting failed while uploading content. The workspaces were reverted to their previous environment. Please try again.',
          'error',
          error
        );
      }
    }
    throw error;
  }

  return reattachedWorkspaceResults;
};

const prepareConnectionsAndWorkflows = async (
  primaryWorkspaceResult: ReattachWorkspaceResult,
  reattachedWorkspaceResults: ReattachWorkspaceResult[]
) => {
  const workspacesNeedingConnections: CopilotStudioWorkspace[] = [];
  let boundConnectionCount = 0;
  let enabledWorkflowCount = 0;
  for (const reattachedWorkspaceResult of reattachedWorkspaceResults) {
    const reattachedWorkspace = reattachedWorkspaceResult.workspace;
    clearAuthAccountState(
      reattachedWorkspace.syncInfo?.accountInfo?.accountId,
      reattachedWorkspace.syncInfo?.accountInfo?.accountEmail
    );
    const connectionResult = await autoBindAgentConnections(reattachedWorkspace, true);
    if (connectionResult.needsNewCount > 0) {
      workspacesNeedingConnections.push(reattachedWorkspace);
    }
    boundConnectionCount += connectionResult.boundCount;
    enabledWorkflowCount += connectionResult.enabledWorkflowCount;
    if (connectionResult.disabledWorkflowNames.length > 0) {
      logger.logWarning(
        TelemetryEventsKeys.ReattachAgentWarning,
        `These workflows are disabled. Bind their connections, then enable them from the connection manager: ${formatPii(
          connectionResult.disabledWorkflowNames.join(', '),
          PiiRedactionType.WorkflowNames
        )}`
      );
    }
  }

  logAIPromptIssues(primaryWorkspaceResult.response.aiPromptResponse);
  return {
    boundConnectionCount,
    enabledWorkflowCount,
    workspacesNeedingConnections,
  };
};

const buildSuccessMessage = (
  primaryWorkspaceResult: ReattachWorkspaceResult,
  reattachedWorkspaceResults: ReattachWorkspaceResult[],
  connectionSummary: ReattachConnectionSummary
): string => {
  const operationVerb = primaryWorkspaceResult.wasRetarget ? 'retargeted' : 'reattached';
  const workspaceKind =
    primaryWorkspaceResult.workspace.type === WorkspaceType.ComponentCollection
      ? 'Component collection'
      : 'Agent';
  const workspaceDisplayName = formatPii(
    primaryWorkspaceResult.workspace.displayName,
    PiiRedactionType.AgentName
  );
  const componentCollectionCount = reattachedWorkspaceResults.filter(
    result => result.workspace.type === WorkspaceType.ComponentCollection
  ).length;
  let successMessage =
    primaryWorkspaceResult.workspace.type === WorkspaceType.Agent &&
    componentCollectionCount > 0
      ? `${workspaceKind} ${workspaceDisplayName} and ${componentCollectionCount} component collection${
          componentCollectionCount === 1 ? '' : 's'
        } ${operationVerb} successfully.`
      : `${workspaceKind} ${workspaceDisplayName} ${operationVerb} successfully.`;
  if (connectionSummary.workspacesNeedingConnections.length === 0) {
    if (connectionSummary.boundConnectionCount > 0) {
      successMessage += ' Connections were bound to existing cloud connections.';
    }
    if (connectionSummary.enabledWorkflowCount > 0) {
      successMessage += ` ${connectionSummary.enabledWorkflowCount} workflow${
        connectionSummary.enabledWorkflowCount === 1 ? ' was' : 's were'
      } enabled.`;
    }
  }
  return successMessage;
};

const performReattachFlow = async (
  context: vscode.ExtensionContext,
  workspace: CopilotStudioWorkspace,
  isAttached: boolean,
  pickedEnvironment: ReattachEnvironmentPickItem,
  selectedAccount: Partial<AccountInfo> | undefined
) => {
  const targetEnvironmentName = pickedEnvironment.label || 'the selected environment';

  // Validate referenced workspaces.
  const plan = buildReattachPlan(workspace);
  if (plan.missingCollectionDirectories.length > 0) {
    const message = `Cannot retarget agent because ${
      plan.missingCollectionDirectories.length
    } referenced component collection workspace(s) were not found: ${formatPii(
      plan.missingCollectionDirectories.join(', '),
      PiiRedactionType.FileUri
    )}`;
    throw new ReattachError(message, 'warning');
  }

  // Handle same-environment refresh.
  if (
    isAttached &&
    workspace.syncInfo?.environmentId &&
    pickedEnvironment.environment.environmentId === workspace.syncInfo.environmentId
  ) {
    const remoteCheckResponse = await lspClient.sendRequest<ReattachAgentResponse>(
      LspMethods.REATTACH_AGENT,
      {
        ...(await buildLspRequestPayload(
          undefined,
          pickedEnvironment.environment,
          selectedAccount,
          true
        )),
        workspaceUri: workspace.workspaceUri,
        allowRetarget: true,
        conflictResolution: RetargetConflictResolution.Prompt,
        checkRemoteAgentOnly: true,
      }
    );

    if (remoteCheckResponse.remoteAgentExists) {
      const refresh = 'Refresh';
      const choice = await vscode.window.showWarningMessage(
        `This agent (${workspace.displayName}) already exists in '${targetEnvironmentName}'. Refresh from the cloud?`,
        { modal: true },
        refresh
      );
      if (choice !== refresh) {
        throw new ReattachCancelledError(
          'Retarget agent canceled because the connected remote agent already exists.'
        );
      }

      const virtualKnowledgeProvider = await registerVirtualKnowledgeProvider(context, workspace);
      const synchronizer = getOrAddSynchronizer(workspace);
      await synchronizer.pull(virtualKnowledgeProvider, {
        suppressErrorNotification: true,
        suppressSuccessNotification: true,
        account: selectedAccount,
      });
      return {
        message: 'Retarget agent completed by refreshing the agent from its current environment.',
        workspacesNeedingConnections: [],
      };
    }
    // The connected remote agent no longer exists, so continue with reattach.
  }

  await confirmReattach(workspace, plan, targetEnvironmentName, isAttached);
  await validateDiagnostics(plan, isAttached);
  const reattachedWorkspaceResults = await executeReattach(
    context,
    plan,
    pickedEnvironment.environment,
    targetEnvironmentName,
    selectedAccount
  );
  const primaryWorkspaceResult = reattachedWorkspaceResults.find(
    workspaceResult => workspaceResult.workspace.workspaceUri === workspace.workspaceUri
  );
  if (!primaryWorkspaceResult) {
    throw new Error('The primary workspace result was missing.');
  }
  const connectionSummary = await prepareConnectionsAndWorkflows(
    primaryWorkspaceResult,
    reattachedWorkspaceResults
  );

  return {
    message: buildSuccessMessage(
      primaryWorkspaceResult,
      reattachedWorkspaceResults,
      connectionSummary
    ),
    workspacesNeedingConnections: connectionSummary.workspacesNeedingConnections,
  };
};

const logCommandOutcome = (
  outcome: ReattachOutcome
): ReattachOutcomeInteraction | undefined => {
  if (outcome.kind === 'success') {
    logger.logInfo(TelemetryEventsKeys.ReattachAgentSuccess, outcome.message);
    return;
  }
  if (outcome.kind === 'cancelled') {
    logger.logWarning(TelemetryEventsKeys.ReattachAgentWarning, undefined, {
      message: outcome.message,
    });
    return;
  }

  const { error } = outcome;
  if (error instanceof ReattachError) {
    const { action, reason } = error;
    const errorData = reason !== undefined ? { error: reason } : undefined;
    if (!action) {
      if (error.level === 'warning') {
        logger.logWarning(TelemetryEventsKeys.ReattachAgentWarning, error.message, errorData);
      } else {
        logger.logError(TelemetryEventsKeys.ReattachAgentError, error.message, errorData);
      }
      return;
    }

    const logData = {
      message: error.message,
      ...(reason !== undefined ? { error: reason } : {}),
    };
    const displayMessage = prepareLogData(error.message, {}).displayMessage ?? error.message;

    let selection: Thenable<string | undefined>;
    if (error.level === 'warning') {
      logger.logWarning(TelemetryEventsKeys.ReattachAgentWarning, undefined, logData);
      selection = vscode.window.showWarningMessage(displayMessage, action.label);
    } else {
      logger.logError(TelemetryEventsKeys.ReattachAgentError, undefined, logData);
      selection = vscode.window.showErrorMessage(displayMessage, action.label);
    }
    return { selection, action };
  } else {
    logger.logError(TelemetryEventsKeys.ReattachAgentError, 'Error reattaching agent', {
      error,
    });
  }
};

const completeOutcomeInteraction = async (
  interaction: ReattachOutcomeInteraction | undefined
): Promise<void> => {
  if (interaction && await interaction.selection === interaction.action.label) {
    await vscode.commands.executeCommand(interaction.action.command);
  }
};

export const executeReattachAgentCommand = async (
  context: vscode.ExtensionContext,
  treeItem?: { workspace?: CopilotStudioWorkspace }
): Promise<void> => {
  logger.logInfo(TelemetryEventsKeys.ReattachAgentClick, undefined, {
    message: 'Reattach agent initiated',
  });

  let executionResult: ReattachExecutionResult = {};
  try {
    const activeSyncUri = getActiveSyncUri();
    if (activeSyncUri !== undefined) {
      throw new ReattachError(
        'A sync is already in progress. Please wait for it to finish before retargeting an agent.',
        'warning'
      );
    }

    const workspace = await selectReattachWorkspace(treeItem);
    const isAttached = hasConnectionFileInWorkspace(workspace.workspaceUri);
    const pickedEnvironment = await pickTargetEnvironment(isAttached);
    const selectedAccount = pickedEnvironment.sourceAccount ?? getPreferredTreeAccount();
    executionResult = await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: isAttached ? 'Retargeting Agent...' : 'Reattaching Agent...',
        cancellable: false,
      },
      () =>
        withSyncCommandBusy(workspace.workspaceUri, async () => {
          let outcome: ReattachOutcome;
          let result: Awaited<ReturnType<typeof performReattachFlow>> | undefined;
          try {
            result = await performReattachFlow(
              context,
              workspace,
              isAttached,
              pickedEnvironment,
              selectedAccount
            );
            outcome = { kind: 'success', message: result.message };
          } catch (error) {
            outcome = error instanceof ReattachCancelledError
              ? { kind: 'cancelled', message: error.message }
              : { kind: 'failed', error };
          }

          return {
            outcomeInteraction: logCommandOutcome(outcome),
            workspacesNeedingConnections: result?.workspacesNeedingConnections,
          };
        })
    );
  } catch (error) {
    executionResult.outcomeInteraction = logCommandOutcome(
      error instanceof ReattachCancelledError
        ? { kind: 'cancelled', message: error.message }
        : { kind: 'failed', error }
    );
  }

  await completeOutcomeInteraction(executionResult.outcomeInteraction);

  if (executionResult.workspacesNeedingConnections) {
    await promptManageConnectionsForWorkspaces(context, executionResult.workspacesNeedingConnections);
  }
};

export const registerReattachAgentCommand = (context: vscode.ExtensionContext) => {
  const command = vscode.commands.registerCommand(
    'microsoft-copilot-studio.reattachAgent',
    async (treeItem?: { workspace?: CopilotStudioWorkspace }) =>
      await executeReattachAgentCommand(context, treeItem)
  );

  context.subscriptions.push(command);
};
