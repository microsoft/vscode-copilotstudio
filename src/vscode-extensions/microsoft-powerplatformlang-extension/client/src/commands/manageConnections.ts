import * as vscode from 'vscode';
import { CopilotStudioWorkspace, getWorkspaceByUri } from '../sync/localWorkspaces';
import { selectWorkspace } from '../sync/workspacePicker';
import { ConnectionManagerController } from '../connections/connectionManager';
import { declareConnectionReferences } from '../connections/connectionCatalog';
import { TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';

type ManageConnectionsArg =
  | { workspace: CopilotStudioWorkspace }
  | { ws: CopilotStudioWorkspace }
  | CopilotStudioWorkspace
  | undefined;

const resolveWorkspace = async (arg: ManageConnectionsArg): Promise<CopilotStudioWorkspace | undefined> => {
  if (arg && typeof arg === 'object') {
    if ('workspace' in arg && arg.workspace) {
      return arg.workspace;
    }
    if ('ws' in arg && arg.ws) {
      return arg.ws;
    }
    if ('workspaceUri' in arg && arg.workspaceUri) {
      return arg;
    }
  }

  const activeUri = vscode.window.activeTextEditor?.document.uri;
  if (activeUri) {
    const fromActive = getWorkspaceByUri(activeUri);
    if (fromActive) {
      return fromActive;
    }
  }

  return selectWorkspace();
};

export const registerManageConnectionsCommand = (context: vscode.ExtensionContext) => {
  const command = vscode.commands.registerCommand(
    'microsoft-copilot-studio.manageConnections',
    async (arg?: ManageConnectionsArg) => {
      const workspace = await resolveWorkspace(arg);
      if (!workspace) {
        void vscode.window.showInformationMessage('Open or select a connected agent to manage its connections.');
        return;
      }
      if (!workspace.syncInfo) {
        void vscode.window.showWarningMessage('This agent is not connected to an environment. Reattach the agent before managing connections.');
        return;
      }

      logger.logInfo(TelemetryEventsKeys.ConnectionCreationInfo);
      await ConnectionManagerController.show(context, workspace);
    }
  );

  context.subscriptions.push(command);
};

export const registerDeclareConnectionReferenceCommand = (context: vscode.ExtensionContext) => {
  const command = vscode.commands.registerCommand(
    'microsoft-copilot-studio.declareConnectionReference',
    async (logicalName?: string) => {
      if (!logicalName || !logicalName.trim()) {
        return;
      }
      const activeUri = vscode.window.activeTextEditor?.document.uri;
      const workspace = activeUri ? getWorkspaceByUri(activeUri) : await selectWorkspace();
      if (!workspace?.syncInfo) {
        void vscode.window.showWarningMessage('Connect this agent to an environment before declaring connection references.');
        return;
      }

      try {
        const result = await declareConnectionReferences(workspace, [logicalName.trim()]);
        if (result.invalid.length > 0) {
          void vscode.window.showErrorMessage(`Couldn't declare connection reference '${logicalName.trim()}'. A connector could not be determined from the name — it must contain a connector segment such as 'shared_office365'.`);
          return;
        }
        void vscode.window.showInformationMessage(`Connection reference '${logicalName.trim()}' declared.`);
      } catch (error) {
        const message = (error as Error).message ?? 'Failed to declare the connection reference.';
        logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to declare connection reference: <pii>${message}</pii>`);
        void vscode.window.showErrorMessage(message);
      }
    }
  );

  context.subscriptions.push(command);
};
