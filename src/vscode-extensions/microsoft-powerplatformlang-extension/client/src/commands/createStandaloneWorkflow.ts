import * as vscode from 'vscode';
import { TelemetryEventsKeys } from '../constants';
import { createStandaloneWorkflow, reportStandaloneWorkflowFailure, resolveStandaloneWorkflowProject } from '../workflows/standaloneWorkflows';
import { openClonedWorkspace } from '../startup/postOpen';

const pickName = async (): Promise<string | undefined> =>
  (await vscode.window.showInputBox({
    title: 'Create workflow',
    prompt: 'Name the new workflow',
    placeHolder: 'My Flow',
    validateInput: value => (value && value.trim().length > 0 ? undefined : 'A name is required.'),
  }))?.trim();

const pickFolder = async (): Promise<vscode.Uri | undefined> =>
  (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, openLabel: 'Create here' }))?.[0];

/**
 * Registers the command that scaffolds a new standalone workflow workspace.
 */
export const registerCreateStandaloneWorkflowCommand = (context: vscode.ExtensionContext) => {
  const command = vscode.commands.registerCommand('microsoft-copilot-studio.createStandaloneWorkflow', async () => {
    let workflowName: string | undefined;

    try {
      const name = await pickName();

      if (!name) {
        return;
      }

      workflowName = name;

      const folder = await pickFolder();

      if (!folder) {
        return;
      }

      const response = await createStandaloneWorkflow(name, folder.fsPath);
      const { projectRoot, entryFile } = resolveStandaloneWorkflowProject(folder, response);

      await openClonedWorkspace(context, projectRoot, entryFile, { workflowId: response.workflowId });
    } catch (error) {
      reportStandaloneWorkflowFailure(
        TelemetryEventsKeys.CloneAgentError,
        'Error creating workflow',
        error,
        'The workflow could not be created.',
        workflowName);
    }
  });

  context.subscriptions.push(command);
};
