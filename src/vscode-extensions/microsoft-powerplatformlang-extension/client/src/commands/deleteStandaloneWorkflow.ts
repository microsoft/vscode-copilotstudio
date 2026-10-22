import * as vscode from 'vscode';
import { TelemetryEventsKeys } from '../constants';
import { deleteStandaloneWorkflow, getStandaloneWorkflowAgentUse, reportStandaloneWorkflowFailure } from '../workflows/standaloneWorkflows';
import { isCopilotStudioTreeItem, TreeItemKind } from '../clone/tree';

const DELETE_DETAIL = 'This deletes the cloud flow. Local files are not touched and this cannot be undone.';

/**
 * Deletes the cloud flow a tree item shows once the user confirms a prompt that names the agents using it.
 */
export const confirmAndDeleteStandaloneWorkflow = async (treeItem?: unknown): Promise<void> => {
  if (!isCopilotStudioTreeItem(treeItem) || treeItem.kind !== TreeItemKind.Workflow) {
    return;
  }

  const { workflow, environment } = treeItem;
  const account = treeItem.sourceAccount ?? {};

  try {
    const confirmation = await vscode.window.showWarningMessage(`Delete "${workflow.name}" from ${environment.displayName}?`, { modal: true, detail: [(await getStandaloneWorkflowAgentUse(environment, account, workflow.workflowId)).warning, DELETE_DETAIL].filter(Boolean).join('\n\n') }, 'Delete');

    if (confirmation !== 'Delete') {
      return;
    }

    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Deleting ${workflow.name}`, cancellable: true }, (_progress, token) => deleteStandaloneWorkflow(environment, account, workflow.workflowId, token));

    void vscode.window.showInformationMessage(`${workflow.name} was deleted from ${environment.displayName}. Any local copy is still here.`);
    await vscode.commands.executeCommand('microsoft-copilot-studio.refreshAgentTreeView');
  } catch (error) {
    reportStandaloneWorkflowFailure(TelemetryEventsKeys.CloneAgentError, 'Error deleting cloud flow', error, 'The cloud flow could not be deleted.', workflow.name, environment.environmentId);
  }
};

/**
 * Registers the command that deletes one cloud flow from its environment.
 */
export const registerDeleteStandaloneWorkflowCommand = (context: vscode.ExtensionContext) => {
  context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.deleteStandaloneWorkflow', confirmAndDeleteStandaloneWorkflow));
};
