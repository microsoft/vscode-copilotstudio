import * as vscode from 'vscode';
import logger, { formatEnvironmentId } from '../services/logger';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { buildEnvironmentPickItems, EnvironmentPickItem, pickAccount } from '../services/accountEnvPicker';
import { listEnvironmentsAsync } from '../clients/bapClient';
import { cloneStandaloneWorkflow, listStandaloneWorkflows, reportStandaloneWorkflowFailure, resolveStandaloneWorkflowProject, StandaloneWorkflowView } from '../workflows/standaloneWorkflows';
import { isCopilotStudioTreeItem, TreeItemKind } from '../clone/tree';
import { openClonedWorkspace } from '../startup/postOpen';
import { AccountInfo, EnvironmentInfo } from '../types';

type WorkflowPickItem = vscode.QuickPickItem & { workflow: StandaloneWorkflowView };

const pickEnvironment = async (account: AccountInfo): Promise<EnvironmentInfo | undefined> => {
  const environments = await listEnvironmentsAsync(DefaultCoreServicesClusterCategory, null, account.accountId ?? null, account.accountEmail, true);
  const picked = await vscode.window.showQuickPick(buildEnvironmentPickItems(environments, account), { title: 'Select environment', placeHolder: 'Choose an environment' });

  return picked && 'environment' in picked ? (picked as EnvironmentPickItem).environment : undefined;
};

const pickWorkflow = async (workflows: StandaloneWorkflowView[]): Promise<StandaloneWorkflowView | undefined> => {
  if (workflows.length === 0) {
    void vscode.window.showInformationMessage('This environment holds no cloud flows.');

    return undefined;
  }

  const items: WorkflowPickItem[] = workflows.map(workflow => ({
    label: workflow.name,
    description: workflow.isActivated ? 'Activated' : 'Draft',
    detail: workflow.description,
    workflow,
  }));

  return (await vscode.window.showQuickPick(items, { title: 'Select cloud flow', placeHolder: 'Choose a cloud flow to clone' }))?.workflow;
};

const pickFolder = async (): Promise<vscode.Uri | undefined> =>
  (await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, canSelectMany: false, openLabel: 'Clone here' }))?.[0];

type WorkflowSelection = { environment: EnvironmentInfo; account: Partial<AccountInfo>; workflow: StandaloneWorkflowView };

const selectWorkflow = async (treeItem?: unknown): Promise<WorkflowSelection | undefined> => {
  if (isCopilotStudioTreeItem(treeItem) && treeItem.kind === TreeItemKind.Workflow) {
    return { environment: treeItem.environment, account: treeItem.sourceAccount ?? {}, workflow: treeItem.workflow };
  }

  const account = await pickAccount('Choose the account to browse for cloud flows');

  if (!account || account === 'cancelled') {
    return undefined;
  }

  const environment = await pickEnvironment(account);

  if (!environment) {
    return undefined;
  }

  const workflows = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: 'Loading cloud flows', cancellable: true },
    (_progress, token) => listStandaloneWorkflows(environment, account, undefined, undefined, token));

  const workflow = await pickWorkflow(workflows);

  return workflow ? { environment, account, workflow } : undefined;
};

/**
 * Registers the command that clones one cloud flow into a standalone workflow workspace.
 */
export const registerCloneStandaloneWorkflowCommand = (context: vscode.ExtensionContext) => {
  const command = vscode.commands.registerCommand('microsoft-copilot-studio.cloneStandaloneWorkflow', async (treeItem?: unknown) => {
    let workflowName: string | undefined;

    try {
      const selection = await selectWorkflow(treeItem);

      if (!selection) {
        return;
      }

      const { environment, account, workflow } = selection;

      workflowName = workflow.name;

      const folder = await pickFolder();

      if (!folder) {
        return;
      }

      const response = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Cloning ${workflow.name}`, cancellable: true },
        (_progress, token) => cloneStandaloneWorkflow(environment, account, workflow.workflowId, folder.fsPath, token));

      const environmentId = formatEnvironmentId(environment.environmentId);

      logger.logInfo(TelemetryEventsKeys.CloneAgentSuccess, undefined, { environmentId });

      const { projectRoot, entryFile } = resolveStandaloneWorkflowProject(folder, response);

      await openClonedWorkspace(context, projectRoot, entryFile, { environmentId, workflowId: workflow.workflowId });
    } catch (error) {
      reportStandaloneWorkflowFailure(
        TelemetryEventsKeys.CloneAgentError,
        'Error cloning cloud flow',
        error,
        'The cloud flow could not be cloned.',
        workflowName);
    }
  });

  context.subscriptions.push(command);
};
