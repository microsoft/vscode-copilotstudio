import * as vscode from 'vscode';
import { addWorkspaceChangeSubscription, buildAgentIdentityTooltip, getWorkspaceByUri } from '../sync/localWorkspaces';

export const registerAgentStatusBar = (context: vscode.ExtensionContext): void => {
  const statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  context.subscriptions.push(statusBarItem);

  const update = () => {
    const activeUri = vscode.window.activeTextEditor?.document.uri;
    const workspace = activeUri ? getWorkspaceByUri(activeUri) : undefined;
    if (!workspace) {
      statusBarItem.hide();
      return;
    }

    const parts = [workspace.displayName];
    if (workspace.schemaName) {
      parts.push(workspace.schemaName);
    }
    const environmentId = workspace.syncInfo?.environmentId;
    if (environmentId) {
      parts.push(environmentId);
    }
    statusBarItem.text = `$(hexagon) ${parts.join(' · ')}`;
    statusBarItem.tooltip = buildAgentIdentityTooltip(workspace);
    statusBarItem.show();
  };

  context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(() => update()));
  context.subscriptions.push(addWorkspaceChangeSubscription(() => update()));
  update();
};
