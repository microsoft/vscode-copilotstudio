import * as vscode from 'vscode';
import { AccountInfo, EnvironmentInfo, ReattachAgentRequest, ReattachAgentResponse, RetargetConflictResolution, FinalizeRetargetResponse } from '../types';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { listEnvironmentsAsync } from '../clients/bapClient';
import { switchAccount, getPreferredTreeAccount, listStoredAccounts } from '../clients/account';
import { pushNewWorkspace } from '../sync/workspaceScm';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';
import { logAIPromptIssues, withSyncCommandBusy, getActiveSyncUri } from '../sync/workspaceSynchronizer';
import { hasConnectionFileInWorkspace, WorkspaceType, CopilotStudioWorkspace } from '../sync/localWorkspaces';
import { selectWorkspace } from '../sync/workspacePicker';
import { getDiagnosticsErrors } from './syncWorkspace';
import { autoBindAgentConnections, promptManageConnections } from '../connections/connectionManager';

type ReattachEnvironmentPickItem = vscode.QuickPickItem & {
  environment: EnvironmentInfo;
  sourceAccount?: AccountInfo;
};

type ReattachAccountPickItem = vscode.QuickPickItem & {
  account: AccountInfo;
};

export const registerReattachAgentCommand = (context: vscode.ExtensionContext) => {
  const reattachAgentCommand = vscode.commands.registerCommand('microsoft-copilot-studio.reattachAgent', async (treeItem?: { workspace?: CopilotStudioWorkspace }) => {
    if (getActiveSyncUri() !== undefined) {
      void vscode.window.showWarningMessage('A sync is already in progress. Please wait for it to finish before retargeting an agent.');
      return;
    }

    const currentWorkspace = treeItem?.workspace ?? await selectWorkspace();
    if (!currentWorkspace) {
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
          account.accountEmail
        );
        const seen = new Set<string>();
        const items: vscode.QuickPickItem[] = [];
        let lastSku: string | undefined;
        for (const env of envs) {
          if (seen.has(env.environmentId)) {
            continue;
          }
          seen.add(env.environmentId);
          const sku = env.environmentSku || 'Other';
          if (sku !== lastSku) {
            items.push({ label: sku, kind: vscode.QuickPickItemKind.Separator });
            lastSku = sku;
          }
          const item: ReattachEnvironmentPickItem = {
            label: env.displayName,
            description: env.environmentId,
            environment: env,
            sourceAccount: account
          };
          items.push(item);
        }
        quickPick.items = items;
      } catch (error: any) {
        logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[Reattach] Failed to load environments: <pii>${error?.message || error}</pii>`);
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

      if (isAttached && sourceEnvironmentId && environmentInfo.environmentId === sourceEnvironmentId) {
        const refresh = 'Refresh';
        const choice = await vscode.window.showInformationMessage(`This agent (${agentDisplayName}) is already attached to '${targetEnvironmentName}'. Refresh from the cloud instead?`, { modal: true }, refresh);
        if (choice === refresh) {
          await vscode.commands.executeCommand('microsoft-copilot-studio.getChanges', { ws: currentWorkspace });
        }
        return;
      }

      if (isAttached) {
        const retarget = 'Retarget';
        const choice = await vscode.window.showWarningMessage(`Retarget this agent (${agentDisplayName}) to '${targetEnvironmentName}'? Your local content will be uploaded to '${targetEnvironmentName}' and the agent will be connected there.`, { modal: true }, retarget);
        if (choice !== retarget) {
          return;
        }

        const diagnostics = await getDiagnosticsErrors(currentWorkspace);
        if (diagnostics.count > 0) {
          const errorMessage = `Cannot retarget agent: found ${diagnostics.count} error(s) in ${diagnostics.files} file(s). Fix the errors and try again.`;
          logger.logWarning(TelemetryEventsKeys.ReattachAgentError, undefined, { message: errorMessage });
          const detailView = await vscode.window.showErrorMessage(errorMessage, 'View Details');
          if (detailView === 'View Details') {
            await vscode.commands.executeCommand('workbench.actions.view.problems');
          }
          return;
        }
      }

      let workspaceNeedingConnections: CopilotStudioWorkspace | undefined;

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: isAttached ? "Retargeting Agent..." : "Reattaching Agent...",
          cancellable: false
        },
        async () => {
          await withSyncCommandBusy(workspaceUri, async () => {
            try {
              const selectedAccount = pickedEnvironment.sourceAccount ?? getPreferredTreeAccount();
              const basePayload = await buildLspRequestPayload(undefined, environmentInfo, selectedAccount);

              const sendReattach = async (resolution: RetargetConflictResolution): Promise<ReattachAgentResponse> => {
                const reattachRequest: ReattachAgentRequest = {
                  ...basePayload,
                  workspaceUri,
                  allowRetarget: isAttached,
                  conflictResolution: resolution
                };
                return await lspClient.sendRequest<ReattachAgentResponse>(LspMethods.REATTACH_AGENT, reattachRequest);
              };

              let reattachResult = await sendReattach(RetargetConflictResolution.Prompt);
              while (reattachResult.code === 200 && reattachResult.schemaConflict) {
                const reuseExisting = 'Reuse existing';
                const choice = await vscode.window.showWarningMessage(`An agent with the same schema name already exists in '${targetEnvironmentName}'. Reattach to the existing agent and update it with your local content?`, { modal: true }, reuseExisting);
                if (choice !== reuseExisting) {
                  return;
                }
                reattachResult = await sendReattach(RetargetConflictResolution.ReuseExisting);
              }

              if (reattachResult.code !== 200) {
                logger.logError(TelemetryEventsKeys.ReattachAgentError, `Reattach failed: <pii>${reattachResult.message ?? 'Unknown error'}</pii>`);
                return;
              }

              const reattachedWorkspace: CopilotStudioWorkspace = {
                workspaceUri,
                displayName: agentDisplayName,
                description: '',
                icon: new vscode.ThemeIcon('symbol-key'),
                type: WorkspaceType.Agent,
                syncInfo: reattachResult.agentSyncInfo
              };

              if (reattachResult.requiresLocalPush) {
                try {
                  await pushNewWorkspace(context, reattachedWorkspace, isAttached);
                } catch (pushError) {
                  if (isAttached) {
                    try {
                      await lspClient.sendRequest<FinalizeRetargetResponse>(LspMethods.FINALIZE_RETARGET, { workspaceUri, pushSucceeded: false });
                      logger.logError(TelemetryEventsKeys.ReattachAgentError, `Retarget push failed; reverted to its previous environment: <pii>${(pushError as Error).message}</pii>`);
                      void vscode.window.showErrorMessage(`Retargeting failed while uploading content. The agent was reverted to its previous environment. Please try again.`);
                    } catch (rollbackError) {
                      logger.logError(TelemetryEventsKeys.ReattachAgentError, `Retarget push failed and rollback to the previous environment failed: <pii>${(rollbackError as Error).message}</pii>`);
                    }
                    return;
                  }
                  throw pushError;
                }

                if (isAttached) {
                  try {
                    await lspClient.sendRequest<FinalizeRetargetResponse>(LspMethods.FINALIZE_RETARGET, { workspaceUri, pushSucceeded: true });
                  } catch (finalizeError) {
                    logger.logWarning(TelemetryEventsKeys.ReattachAgentError, `Retarget succeeded but clearing the retarget backup failed; the agent remains on its new environment: <pii>${(finalizeError as Error).message}</pii>`);
                  }
                }
              }
              logger.logInfo(TelemetryEventsKeys.ReattachAgentInfo, `Agent <pii>${reattachResult.agentSyncInfo.agentId}</pii> ${isAttached ? 'retargeted' : 'reattached'} successfully.`);

              const autoBindResult = await autoBindAgentConnections(reattachedWorkspace, true);
              if (autoBindResult.needsNewCount > 0) {
                workspaceNeedingConnections = reattachedWorkspace;
              } else {
                const parts: string[] = [];
                if (autoBindResult.boundCount > 0) {
                  parts.push('Agent connections were bound to existing cloud connections.');
                }
                if (autoBindResult.enabledWorkflowCount > 0) {
                  parts.push(`${autoBindResult.enabledWorkflowCount} workflow${autoBindResult.enabledWorkflowCount === 1 ? ' was' : 's were'} enabled.`);
                }
                if (parts.length > 0) {
                  void vscode.window.showInformationMessage(parts.join(' '));
                }
              }

              if (autoBindResult.disabledWorkflowNames.length > 0) {
                logger.logWarning(TelemetryEventsKeys.ReattachAgentInfo, `These workflows are disabled. Bind their connections, then enable them from the connection manager: <pii>${autoBindResult.disabledWorkflowNames.join(', ')}</pii>`);
              }
              logAIPromptIssues(reattachResult.aiPromptResponse);
            } catch (error) {
              logger.logError(TelemetryEventsKeys.ReattachAgentError, `Error reattaching agent: <pii>${(error as Error).message}</pii>`);
            }
          });
        }
      );

      if (workspaceNeedingConnections) {
        await promptManageConnections(context, workspaceNeedingConnections);
      }
    });
    
    quickPick.onDidHide(() => quickPick.dispose());

    await loadAccountsOrEnvironments();
    quickPick.show();
  });

  context.subscriptions.push(reattachAgentCommand);
};
