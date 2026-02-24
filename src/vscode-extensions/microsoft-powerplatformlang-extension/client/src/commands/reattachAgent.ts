import * as vscode from 'vscode';
import { EnvironmentInfo, ReattachAgentRequest, ReattachAgentResponse} from '../types';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { listEnvironmentsAsync } from '../clients/bapClient';
import { switchAccount } from '../clients/account';
import { pushNewWorkspace } from '../sync/workspaceScm';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';

export const registerReattachAgentCommand = (context: vscode.ExtensionContext) => {
  const reattachAgentCommand = vscode.commands.registerCommand('microsoft-copilot-studio.reattachAgent', async () => {
    const quickPick = vscode.window.createQuickPick();
    quickPick.title = 'Select environment to reattach';
    quickPick.placeholder = 'Choose an environment';
    quickPick.busy = true;
    quickPick.buttons = [
      { iconPath: new vscode.ThemeIcon("sign-in"), tooltip: "Switch account" }
    ];

    let environments: EnvironmentInfo[] = [];
    const loadEnvironments = async () => {
      environments = await listEnvironmentsAsync(DefaultCoreServicesClusterCategory, null, null);
      quickPick.items = environments.map(env => ({
        label: env.displayName,
        description: env.environmentId,
        environment: env
      }));
      quickPick.busy = false;
    };

    quickPick.onDidTriggerButton(async (button) => {
      if (button.tooltip === "Switch account") {
        await switchAccount(DefaultCoreServicesClusterCategory);
        await loadEnvironments();
      }
    });
    
    quickPick.onDidAccept(async () => {
      const pickedEnvironment = quickPick.selectedItems[0] as vscode.QuickPickItem & { environment: EnvironmentInfo };
      quickPick.hide();

      if (!vscode.workspace.workspaceFolders || vscode.workspace.workspaceFolders.length === 0) {        
        return;
      }

      const environmentInfo = pickedEnvironment.environment;
      const workspaceFolder = vscode.workspace.workspaceFolders[0];

      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: "Reattaching Agent...",
          cancellable: false
        },
        async () => {
          try {
            const reattachRequest: ReattachAgentRequest = {
              ...await buildLspRequestPayload(undefined, environmentInfo),
              workspaceUri: workspaceFolder.uri.toString()
            };
            const reattachResult = await lspClient.sendRequest<ReattachAgentResponse>(LspMethods.REATTACH_AGENT, reattachRequest);

            if (reattachResult.isNewAgent) {
              const newWorkspace = {
                workspaceUri: workspaceFolder.uri.toString(),
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
          } catch (error) {
            logger.logError(TelemetryEventsKeys.ReattachAgentError, `Error reattaching agent: ${(error as Error).message}`);
          }
        }
      );
    });
    
    quickPick.onDidHide(() => quickPick.dispose());

    await loadEnvironments();
    quickPick.show();
  });

  context.subscriptions.push(reattachAgentCommand);
};
