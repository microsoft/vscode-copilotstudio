import * as vscode from 'vscode';
import { TelemetryEventsKeys } from '../constants';
import { deleteStandaloneWorkflow, reportStandaloneWorkflowFailure } from '../workflows/standaloneWorkflows';
import { isCopilotStudioTreeItem, TreeItemKind } from '../clone/tree';

/**
 * Registers the command that deletes one cloud flow from its environment.
 */
export const registerDeleteStandaloneWorkflowCommand = (context: vscode.ExtensionContext) => {
  const command = vscode.commands.registerCommand('microsoft-copilot-studio.deleteStandaloneWorkflow', async (treeItem?: unknown) => {
    if (!isCopilotStudioTreeItem(treeItem) || treeItem.kind !== TreeItemKind.Workflow) {
      return;
    }

    const { workflow, environment } = treeItem;
    const confirmation = await vscode.window.showWarningMessage(
      `Delete "${workflow.name}" from ${environment.displayName}?`,
      { modal: true, detail: 'This deletes the cloud flow. Local files are not touched and this cannot be undone.' },
      'Delete');

    if (confirmation !== 'Delete') {
      return;
    }

    try {
      await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Deleting ${workflow.name}`, cancellable: true },
        (_progress, token) => deleteStandaloneWorkflow(environment, treeItem.sourceAccount ?? {}, workflow.workflowId, token));

      void vscode.window.showInformationMessage(`${workflow.name} was deleted from ${environment.displayName}. Any local copy is still here.`);
      await vscode.commands.executeCommand('microsoft-copilot-studio.refreshAgentTreeView');
    } catch (error) {
      reportStandaloneWorkflowFailure(
        TelemetryEventsKeys.CloneAgentError,
        'Error deleting cloud flow',
        error,
        'The cloud flow could not be deleted.',
        workflow.name);
    }
  });

  context.subscriptions.push(command);
};
