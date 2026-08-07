import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { AccountInfo, EnvironmentInfo, ReattachAgentRequest, ReattachAgentResponse, RetargetConflictResolution, FinalizeRetargetResponse } from '../types';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { listEnvironmentsAsync } from '../clients/bapClient';
import { buildEnvironmentPickItems } from '../services/accountEnvPicker';
import { switchAccount, getPreferredTreeAccount, listStoredAccounts, clearAuthAccountState } from '../clients/account';
import { pushNewWorkspace } from '../sync/workspaceScm';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger, { formatPii, PiiRedactionType } from '../services/logger';
import { logAIPromptIssues, withSyncCommandBusy, getActiveSyncUri } from '../sync/workspaceSynchronizer';
import { getAllWorkspaces, hasConnectionFileInWorkspace, WorkspaceType, CopilotStudioWorkspace, getWorkspaceKindLabel } from '../sync/localWorkspaces';
import { selectWorkspace } from '../sync/workspacePicker';
import { getDiagnosticsErrors } from './syncWorkspace';
import { autoBindAgentConnections, promptManageConnectionsForWorkspaces } from '../connections/connectionManager';
import { ReattachPlan, buildReattachPlanCore } from './reattachPlan';

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

type DiagnosticsErrorMessage = {
  displayMessage: string;
  telemetryMessage: string;
};

const getWorkspaceFolderPath = (workspace: CopilotStudioWorkspace): string => vscode.Uri.parse(workspace.workspaceUri).fsPath;

const getWorkspaceIdentity = (workspace: CopilotStudioWorkspace): string => workspace.syncInfo?.agentId ?? workspace.syncInfo?.componentCollectionId ?? workspace.displayName;

const buildReattachPlan = (workspace: CopilotStudioWorkspace): ReattachPlan => {
  const workspaceFolder = getWorkspaceFolderPath(workspace);
  const referencesFilePath = path.join(workspaceFolder, 'references.mcs.yml');
  const referencesContent = workspace.type === WorkspaceType.Agent && fs.existsSync(referencesFilePath) ? fs.readFileSync(referencesFilePath, 'utf-8') : undefined;
  const candidateCollections = getAllWorkspaces().map(candidate => ({ workspace: candidate, folderPath: getWorkspaceFolderPath(candidate) }));
  return buildReattachPlanCore(workspace, workspaceFolder, referencesContent, candidateCollections);
};

const getDiagnosticsErrorMessage = async (workspaces: CopilotStudioWorkspace[], operationName: string): Promise<DiagnosticsErrorMessage | undefined> => {
  for (const workspace of workspaces) {
    const diagnostics = await getDiagnosticsErrors(workspace);
    if (diagnostics.count > 0) {
      return {
        displayMessage: `Cannot ${operationName}: found ${diagnostics.count} error(s) in ${diagnostics.files} file(s) for '${workspace.displayName}'. Fix the errors and try again.`,
        telemetryMessage: `Cannot ${operationName}: found ${diagnostics.count} error(s) in ${diagnostics.files} file(s) for '${workspace.displayName}'. Fix the errors and try again.`
      };
    }
  }

  return undefined;
};

const runReattachForWorkspace = async (context: vscode.ExtensionContext, workspace: CopilotStudioWorkspace, basePayload: Omit<ReattachAgentRequest, 'workspaceUri' | 'allowRetarget' | 'conflictResolution'>, targetEnvironmentName: string): Promise<ReattachWorkspaceResult | undefined> => {
  const workspaceUri = workspace.workspaceUri;
  const wasRetarget = hasConnectionFileInWorkspace(workspaceUri);
  const sendReattach = async (resolution: RetargetConflictResolution): Promise<ReattachAgentResponse> => await lspClient.sendRequest<ReattachAgentResponse>(LspMethods.REATTACH_AGENT, { ...basePayload, workspaceUri, allowRetarget: wasRetarget, conflictResolution: resolution });
  // A component collection referenced by an agent is created by the agent's own reattach, so an
  // existing same-schema collection during a multi-workspace retarget is expected: reuse it
  // silently instead of prompting for a collection that the agent just created.
  let reattachResult = await sendReattach(workspace.type === WorkspaceType.ComponentCollection ? RetargetConflictResolution.ReuseExisting : RetargetConflictResolution.Prompt);
  while (reattachResult.code === 200 && reattachResult.schemaConflict) {
    const reuseExisting = 'Reuse existing';
    const choice = await vscode.window.showWarningMessage(`A ${getWorkspaceKindLabel(workspace)} with the same schema name already exists in '${targetEnvironmentName}'. Reattach to the existing ${getWorkspaceKindLabel(workspace)} and update it with your local content?`, { modal: true }, reuseExisting);
    if (choice !== reuseExisting) {
      return undefined;
    }
    reattachResult = await sendReattach(RetargetConflictResolution.ReuseExisting);
  }

  if (reattachResult.code !== 200) {
    logger.logError(
      TelemetryEventsKeys.ReattachAgentError,
      `Reattach failed for '${workspace.displayName}'`,
      { error: new Error(reattachResult.message ?? 'Unknown error') }
    );
    return undefined;
  }

  const reattachedWorkspace: CopilotStudioWorkspace = { ...workspace, syncInfo: reattachResult.agentSyncInfo };
  if (reattachResult.requiresLocalPush) {
    try {
      await pushNewWorkspace(context, reattachedWorkspace, wasRetarget);
    } catch (pushError) {
      if (wasRetarget) {
        await lspClient.sendRequest<FinalizeRetargetResponse>(LspMethods.FINALIZE_RETARGET, { workspaceUri, pushSucceeded: false });
      }
      throw pushError;
    }
  }

  logger.logInfo(TelemetryEventsKeys.ReattachAgentSuccess, undefined, { message: `${getWorkspaceKindLabel(workspace)} <pii>${getWorkspaceIdentity(reattachedWorkspace)}</pii> ${wasRetarget ? 'retargeted' : 'reattached'} successfully.` });
  return { workspace: reattachedWorkspace, response: reattachResult, wasRetarget };
};

const finalizeRetargets = async (results: ReattachWorkspaceResult[], pushSucceeded: boolean): Promise<void> => {
  const retargets = results.filter(item => item.wasRetarget);
  const failures = (await Promise.allSettled(retargets.map(result => lspClient.sendRequest<FinalizeRetargetResponse>(LspMethods.FINALIZE_RETARGET, { workspaceUri: result.workspace.workspaceUri, pushSucceeded })))).filter((outcome): outcome is PromiseRejectedResult => outcome.status === 'rejected');
  if (failures.length > 0) {
    throw new Error(`Failed to finalize ${failures.length} of ${retargets.length} retarget operation(s): ${failures.map(failure => (failure.reason as Error).message).join('; ')}`);
  }
};

export const registerReattachAgentCommand = (context: vscode.ExtensionContext) => {
  const reattachAgentCommand = vscode.commands.registerCommand('microsoft-copilot-studio.reattachAgent', async (treeItem?: { workspace?: CopilotStudioWorkspace }) => {
    logger.logInfo(TelemetryEventsKeys.ReattachAgentClick, undefined, { message: 'Reattach agent initiated' });

    if (getActiveSyncUri() !== undefined) {
      void vscode.window.showWarningMessage('A sync is already in progress. Please wait for it to finish before retargeting an agent.');
      return;
    }

    const currentWorkspace = treeItem?.workspace ?? await selectWorkspace(workspace => workspace.type !== WorkspaceType.ComponentCollection);
    if (!currentWorkspace || currentWorkspace.type === WorkspaceType.ComponentCollection) {
      return;
    }

    const targetWorkspaceUri = currentWorkspace.workspaceUri;
    const agentDisplayName = currentWorkspace.displayName;
    const isAttached = hasConnectionFileInWorkspace(targetWorkspaceUri);
    const sourceEnvironmentId = currentWorkspace.syncInfo?.environmentId;

    const quickPick = vscode.window.createQuickPick();
    quickPick.title = isAttached ? 'Select environment to retarget' : 'Select environment to reattach';
    quickPick.placeholder = 'Choose an environment';
    quickPick.busy = true;
    quickPick.buttons = [
      { iconPath: new vscode.ThemeIcon("sign-in"), tooltip: "Switch account" }
    ];

    let pickPhase: 'account' | 'environment' = 'environment';

    const loadEnvironmentsForAccount = async (account: AccountInfo) => {
      pickPhase = 'environment';
      quickPick.busy = true;
      quickPick.title = isAttached ? 'Select environment to retarget' : 'Select environment to reattach';
      quickPick.placeholder = 'Choose an environment';
      try {
        const envs = await listEnvironmentsAsync(
          DefaultCoreServicesClusterCategory,
          null,
          account.accountId ?? null,
          account.accountEmail,
          true
        );
        quickPick.items = buildEnvironmentPickItems(envs, account);
      } catch (error: any) {
        logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[Reattach] Failed to load environments for ${formatPii(account.accountEmail ?? account.accountId ?? 'Unknown', PiiRedactionType.EmailAddress)}`, { error });
        quickPick.items = [];
      }
      quickPick.busy = false;
    };

    const loadAccountsOrEnvironments = async () => {
      pickPhase = 'environment';
      quickPick.busy = true;
      const storedAccounts = await listStoredAccounts();
      const accountsToQuery = storedAccounts.map<AccountInfo>(account => ({
        accountId: account.accountId,
        accountEmail: account.accountEmail ?? '',
        tenantId: ''
      }));

      if (accountsToQuery.length === 0) {
        quickPick.items = [];
        quickPick.busy = false;
        return;
      }

      if (accountsToQuery.length === 1) {
        await loadEnvironmentsForAccount(accountsToQuery[0]);
        return;
      }

      pickPhase = 'account';
      quickPick.title = 'Select account';
      quickPick.placeholder = 'Choose an account';
      quickPick.items = accountsToQuery.map<ReattachAccountPickItem>(account => ({
        label: account.accountEmail || account.accountId || 'Account',
        account
      }));
      quickPick.busy = false;
    };

    quickPick.onDidTriggerButton(async (button) => {
      if (button.tooltip === "Switch account") {
        await switchAccount(DefaultCoreServicesClusterCategory);
        await loadAccountsOrEnvironments();
      }
    });
    
    quickPick.onDidAccept(async () => {
      if (pickPhase === 'account') {
        const pickedAccount = quickPick.selectedItems[0] as ReattachAccountPickItem;
        if (!pickedAccount?.account) {
          return;
        }
        await loadEnvironmentsForAccount(pickedAccount.account);
        return;
      }

      const pickedEnvironment = quickPick.selectedItems[0] as ReattachEnvironmentPickItem;
      if (!pickedEnvironment?.environment) {
        return;
      }
      quickPick.hide();

      const environmentInfo = pickedEnvironment.environment;
      const workspaceUri = targetWorkspaceUri;
      const targetEnvironmentName = pickedEnvironment.label || 'the selected environment';
      const reattachPlan = buildReattachPlan(currentWorkspace);


      if (reattachPlan.missingCollectionDirectories.length > 0) {
        void vscode.window.showErrorMessage(`Cannot retarget ${agentDisplayName}: referenced component collection workspace was not found.`);
        logger.logWarning(TelemetryEventsKeys.ReattachAgentError, `Cannot retarget agent because ${reattachPlan.missingCollectionDirectories.length} referenced component collection workspace(s) were not found: ${formatPii(reattachPlan.missingCollectionDirectories.join(', '), PiiRedactionType.FileUri)}`);
        return;
      }

      if (isAttached && sourceEnvironmentId && environmentInfo.environmentId === sourceEnvironmentId) {
        const refresh = 'Refresh';
        const choice = await vscode.window.showInformationMessage(`This agent (${agentDisplayName}) is already attached to '${targetEnvironmentName}'. Refresh from the cloud instead?`, { modal: true }, refresh);
        if (choice === refresh) {
          await vscode.commands.executeCommand('microsoft-copilot-studio.getChanges', { ws: currentWorkspace });
        }
        return;
      }

      const collectionCount = reattachPlan.workspaces.filter(workspace => workspace.type === WorkspaceType.ComponentCollection).length;

      if (isAttached) {
        const retarget = 'Retarget';
        const subject = collectionCount === 0 ? `this agent (${agentDisplayName})` : `this agent (${agentDisplayName}) and ${collectionCount} component collection${collectionCount === 1 ? '' : 's'}`;
        const collectionNote = collectionCount === 0 ? '' : ` Existing component collections with the same name in '${targetEnvironmentName}' will be updated with your local content.`;
        const choice = await vscode.window.showWarningMessage(`Retarget ${subject} to '${targetEnvironmentName}'? Your local content will be uploaded to '${targetEnvironmentName}' and connected there.${collectionNote}`, { modal: true }, retarget);
        if (choice !== retarget) {
          return;
        }
      } else if (collectionCount > 0) {
        const reattach = 'Reattach';
        const choice = await vscode.window.showWarningMessage(`Reattach this agent (${agentDisplayName}) and ${collectionCount} component collection${collectionCount === 1 ? '' : 's'} to '${targetEnvironmentName}'? Your local content will be uploaded, and existing component collections with the same name in '${targetEnvironmentName}' will be updated with your local content.`, { modal: true }, reattach);
        if (choice !== reattach) {
          return;
        }
      }

      if (isAttached) {
        const diagnosticsErrorMessage = await getDiagnosticsErrorMessage(reattachPlan.workspaces, 'retarget agent');
        if (diagnosticsErrorMessage) {
          logger.logWarning(TelemetryEventsKeys.ReattachAgentError, undefined, { message: diagnosticsErrorMessage.telemetryMessage });
          const detailView = await vscode.window.showErrorMessage(diagnosticsErrorMessage.displayMessage, 'View Details');
          if (detailView === 'View Details') {
            await vscode.commands.executeCommand('workbench.actions.view.problems');
          }
          return;
        }
      }

      const workspacesNeedingConnections: CopilotStudioWorkspace[] = [];

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: isAttached ? "Retargeting Agent..." : "Reattaching Agent...",
          cancellable: false
        },
        async () => {
          await withSyncCommandBusy(workspaceUri, async () => {
            const completedRetargets: ReattachWorkspaceResult[] = [];
            try {
              const selectedAccount = pickedEnvironment.sourceAccount ?? getPreferredTreeAccount();
              const basePayload = await buildLspRequestPayload(undefined, environmentInfo, selectedAccount, true);

              for (const workspaceToReattach of reattachPlan.workspaces) {
                const result = await runReattachForWorkspace(context, workspaceToReattach, basePayload, targetEnvironmentName);
                if (!result) {
                  try {
                    await finalizeRetargets(completedRetargets, false);
                  } catch (rollbackError) {
                    logger.logError(TelemetryEventsKeys.ReattachAgentError, 'Retarget rollback failed for one or more workspaces', { error: rollbackError });
                  }
                  return;
                }
                completedRetargets.push(result);
              }

              try {
                await finalizeRetargets(completedRetargets, true);
              } catch (finalizeError) {
                logger.logWarning(TelemetryEventsKeys.ReattachAgentInfo, 'Retarget succeeded but clearing the retarget backup failed; the workspaces remain on the new environment', { error: finalizeError });
              }

              const primaryResult = completedRetargets.find(result => result.workspace.workspaceUri === currentWorkspace.workspaceUri);
              if (!primaryResult) {
                return;
              }

              let anyConnectionsBound = false;
              let workflowsEnabledTotal = 0;
              for (const result of completedRetargets) {
                clearAuthAccountState(result.workspace.syncInfo?.accountInfo?.accountId, result.workspace.syncInfo?.accountInfo?.accountEmail);
                const autoBindResult = await autoBindAgentConnections(result.workspace, true);
                if (autoBindResult.needsNewCount > 0) {
                  workspacesNeedingConnections.push(result.workspace);
                }
                anyConnectionsBound = anyConnectionsBound || autoBindResult.boundCount > 0;
                workflowsEnabledTotal += autoBindResult.enabledWorkflowCount;
                if (autoBindResult.disabledWorkflowNames.length > 0) {
                  logger.logWarning(TelemetryEventsKeys.ReattachAgentInfo, `These workflows are disabled. Bind their connections, then enable them from the connection manager: ${formatPii(autoBindResult.disabledWorkflowNames.join(', '), PiiRedactionType.WorkflowNames)}`);
                }
              }

              logAIPromptIssues(primaryResult.response.aiPromptResponse);

              const collectionCount = completedRetargets.filter(result => result.workspace.type === WorkspaceType.ComponentCollection).length;
              const successVerb = isAttached ? 'retargeted' : 'reattached';
              let successMessage = currentWorkspace.type === WorkspaceType.Agent && collectionCount > 0
                ? `Agent and ${collectionCount} component collection${collectionCount === 1 ? '' : 's'} ${successVerb} successfully.`
                : `${currentWorkspace.type === WorkspaceType.ComponentCollection ? 'Component collection' : 'Agent'} ${successVerb} successfully.`;
              if (workspacesNeedingConnections.length === 0) {
                if (anyConnectionsBound) {
                  successMessage += ' Connections were bound to existing cloud connections.';
                }
                if (workflowsEnabledTotal > 0) {
                  successMessage += ` ${workflowsEnabledTotal} workflow${workflowsEnabledTotal === 1 ? ' was' : 's were'} enabled.`;
                }
              }
              void vscode.window.showInformationMessage(successMessage);
            } catch (error) {
              if (completedRetargets.some(result => result.wasRetarget)) {
                try {
                  await finalizeRetargets(completedRetargets, false);
                  void vscode.window.showErrorMessage(`Retargeting failed while uploading content. The workspaces were reverted to their previous environment. Please try again.`);
                } catch (rollbackError) {
                  logger.logError(TelemetryEventsKeys.ReattachAgentError, 'Retarget failed and rollback to the previous environment failed', { error: rollbackError });
                }
              }
              logger.logError(TelemetryEventsKeys.ReattachAgentError, 'Error reattaching agent', { error });
            }
          });
        }
      );

      if (workspacesNeedingConnections.length > 0) {
        await promptManageConnectionsForWorkspaces(context, workspacesNeedingConnections);
      }
    });
    
    quickPick.onDidHide(() => quickPick.dispose());

    await loadAccountsOrEnvironments();
    quickPick.show();
  });

  context.subscriptions.push(reattachAgentCommand);
};
