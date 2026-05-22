import * as vscode from 'vscode';
import { AccountInfo, EnvironmentInfo, ReattachAgentRequest, ReattachAgentResponse} from '../types';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { listEnvironmentsAsync } from '../clients/bapClient';
import { hasStoredAccount, switchAccount, getPreferredTreeAccount, listStoredAccounts } from '../clients/account';
import { pushNewWorkspace } from '../sync/workspaceScm';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';
import { logWorkflowIssues, logAIPromptIssues, logNewCustomConnectorsRaw, withSyncCommandBusy } from '../sync/workspaceSynchronizer';
import { getActiveAgentAccount, getAllProjectAccounts } from '../sync/localWorkspaces';

type ReattachEnvironmentPickItem = vscode.QuickPickItem & {
  environment: EnvironmentInfo;
  sourceAccount?: AccountInfo;
};

export const registerReattachAgentCommand = (context: vscode.ExtensionContext) => {
  const reattachAgentCommand = vscode.commands.registerCommand('microsoft-copilot-studio.reattachAgent', async () => {
    const quickPick = vscode.window.createQuickPick();
    quickPick.title = 'Select environment to reattach';
    quickPick.placeholder = 'Choose an environment';
    quickPick.busy = true;
    quickPick.buttons = [
      { iconPath: new vscode.ThemeIcon("sign-in"), tooltip: "Switch account" }
    ];

    const loadEnvironments = async () => {
      const preferred = getPreferredTreeAccount();
      const projectAccounts = getAllProjectAccounts();

      let candidateAccounts: (AccountInfo | undefined)[];
      if (preferred) {
        candidateAccounts = [{
          accountId: preferred.accountId,
          accountEmail: preferred.accountEmail ?? '',
          tenantId: ''
        } as AccountInfo];
      } else if (projectAccounts.length > 0) {
        candidateAccounts = projectAccounts;
      } else {
        const active = getActiveAgentAccount();
        if (active) {
          candidateAccounts = [active];
        } else {
          const stored = await listStoredAccounts();
          candidateAccounts = stored.length > 0
            ? stored.map<AccountInfo>(a => ({ accountId: a.accountId, accountEmail: a.accountEmail ?? '', tenantId: '' }))
            : [undefined];
        }
      }

      const signInChecks = await Promise.all(
        candidateAccounts.map(async (acct) => {
          if (!acct) {
            return (await hasStoredAccount()) ? acct : null;
          }
          const hasAccount = await hasStoredAccount(acct.accountId, acct.accountEmail);
          return hasAccount ? acct : null;
        })
      );
      const accountsToQuery = signInChecks.filter((a): a is AccountInfo | undefined => a !== null);

      const perAccountResults = await Promise.all(
        accountsToQuery.map(async (acct) => {
          try {
            const envs = await listEnvironmentsAsync(
              DefaultCoreServicesClusterCategory,
              null,
              acct?.accountId ?? null,
              acct?.accountEmail
            );
            return envs.map<ReattachEnvironmentPickItem>(env => ({
              label: env.displayName,
              description: env.environmentId,
              environment: env,
              sourceAccount: acct
            }));
          } catch (error: any) {
            logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[Reattach] Failed to load environments for ${acct?.accountId ?? 'default'}: ${error?.message || error}`);
            return [] as ReattachEnvironmentPickItem[];
          }
        })
      );

      const seen = new Set<string>();
      const items: ReattachEnvironmentPickItem[] = [];
      for (const list of perAccountResults) {
        for (const item of list) {
          const key = item.environment.environmentId;
          if (seen.has(key)) {
            continue;
          }
          seen.add(key);
          items.push(item);
        }
      }

      quickPick.items = items;
      quickPick.busy = false;
    };

    quickPick.onDidTriggerButton(async (button) => {
      if (button.tooltip === "Switch account") {
        await switchAccount(DefaultCoreServicesClusterCategory);
        await loadEnvironments();
      }
    });
    
    quickPick.onDidAccept(async () => {
      const pickedEnvironment = quickPick.selectedItems[0] as ReattachEnvironmentPickItem;
      quickPick.hide();

      if (!vscode.workspace.workspaceFolders || vscode.workspace.workspaceFolders.length === 0) {        
        return;
      }

      const environmentInfo = pickedEnvironment.environment;
      const workspaceFolder = vscode.workspace.workspaceFolders[0];
      const workspaceUri = workspaceFolder.uri.toString() + '/'; // Ensure trailing slash for consistency with workspace cache entries

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "Reattaching Agent...",
          cancellable: false
        },
        async () => {
          // For sync buttons to be disabled and loading indicators to be visible during the REATTACH_AGENT call.
          await withSyncCommandBusy(workspaceUri, async () => {
            try {
              const selectedAccount = pickedEnvironment.sourceAccount ?? getPreferredTreeAccount();
              const reattachRequest: ReattachAgentRequest = {
                ...await buildLspRequestPayload(undefined, environmentInfo, selectedAccount),
                workspaceUri
              };
              const reattachResult = await lspClient.sendRequest<ReattachAgentResponse>(LspMethods.REATTACH_AGENT, reattachRequest);

              if (reattachResult.isNewAgent) {
                const newWorkspace = {
                  workspaceUri,
                  displayName: 'Reattached Agent',
                  description: '',
                  icon: new vscode.ThemeIcon('symbol-key'),
                  type: 0,
                  syncInfo: reattachResult.agentSyncInfo
                };
                await pushNewWorkspace(context, newWorkspace);
                logger.logInfo(TelemetryEventsKeys.ReattachAgentInfo, `New agent ${reattachResult.agentSyncInfo.agentId} reattached successfully.`);
              } else {
                logger.logInfo(TelemetryEventsKeys.ReattachAgentInfo, `Existing agent ${reattachResult.agentSyncInfo.agentId} reattached successfully.`);
              }

              logWorkflowIssues(reattachResult.workflowResponse);
              logAIPromptIssues(reattachResult.aiPromptResponse);
              logNewCustomConnectorsRaw(reattachResult.newlyCreatedCustomConnectors, workspaceUri);
            } catch (error) {
              logger.logError(TelemetryEventsKeys.ReattachAgentError, `Error reattaching agent: ${(error as Error).message}`);
            }
          });
        }
      );
    });
    
    quickPick.onDidHide(() => quickPick.dispose());

    await loadEnvironments();
    quickPick.show();
  });

  context.subscriptions.push(reattachAgentCommand);
};
