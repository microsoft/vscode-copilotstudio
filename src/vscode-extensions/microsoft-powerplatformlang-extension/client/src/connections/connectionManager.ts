import * as vscode from 'vscode';
import logger from '../services/logger';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { CopilotStudioWorkspace } from '../sync/localWorkspaces';
import { ConnectionBindingRequest, ConnectionReferenceUsage, AgentConnectionView, WorkflowState, WorkflowStatusView } from '../types';
import {
  applyConnectionBindings,
  createConnectionReference,
  declareConnectionReferences,
  listAgentConnections,
  listWorkflowStatus,
  removeConnectionReference,
  setWorkflowStates
} from './connectionCatalog';
import { awaitConnectionCreation, resolveCreatedConnectionId } from './connectionCreation';
import { isCustomConnectorInternalId, waitForCustomConnectorReady } from './connectorReadiness';
import { pickConnector } from './connectorPicker';
import { buildConnectionManagerHtml } from './connectionManagerHtml';

interface ReadyMessage { type: 'ready'; }
interface RefreshMessage { type: 'refresh'; }
interface CreateConnectionRequest { connectionReferenceLogicalName: string; connectorName: string; }
interface ApplyMessage { type: 'apply'; bindings: ConnectionBindingRequest[]; creates: CreateConnectionRequest[]; }
interface EnableWorkflowMessage { type: 'enableWorkflow'; workflowId: string; activate: boolean; }
interface AddReferenceMessage { type: 'addReference'; }
interface DeclareReferenceMessage { type: 'declareReference'; logicalName: string; }
interface DeleteReferenceMessage { type: 'deleteReference'; logicalName: string; }
interface OpenUsageMessage { type: 'openUsage'; filePath: string; }

type WebviewMessage =
  | ReadyMessage
  | RefreshMessage
  | ApplyMessage
  | EnableWorkflowMessage
  | AddReferenceMessage
  | DeclareReferenceMessage
  | DeleteReferenceMessage
  | OpenUsageMessage;

export class ConnectionManagerController {
  private static readonly panels = new Map<string, ConnectionManagerController>();

  public static async show(context: vscode.ExtensionContext, workspace: CopilotStudioWorkspace): Promise<void> {
    const key = workspace.workspaceUri;
    const existing = ConnectionManagerController.panels.get(key);
    if (existing) {
      existing.panel.reveal(vscode.ViewColumn.Active);
      return;
    }
    const controller = new ConnectionManagerController(context, workspace, buildConnectionManagerHtml);
    ConnectionManagerController.panels.set(key, controller);
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];
  private currentViews: AgentConnectionView[] = [];

  private constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly workspace: CopilotStudioWorkspace,
    buildHtml: (webview: vscode.Webview, agentName: string) => string
  ) {
    this.panel = vscode.window.createWebviewPanel(
      'copilotStudioConnectionManager',
      `Connections: ${workspace.displayName}`,
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [context.extensionUri]
      }
    );

    this.panel.webview.html = buildHtml(this.panel.webview, workspace.displayName);
    this.disposables.push(
      this.panel.webview.onDidReceiveMessage((msg: WebviewMessage) => void this.onMessage(msg))
    );
    this.panel.onDidDispose(() => this.dispose(), undefined, this.disposables);
  }

  private async onMessage(msg: WebviewMessage): Promise<void> {
    switch (msg.type) {
      case 'ready':
      case 'refresh':
        await this.loadAndPost();
        return;
      case 'apply':
        await this.applyBindings(msg.bindings, msg.creates);
        return;
      case 'enableWorkflow':
        await this.enableWorkflow(msg.workflowId, msg.activate);
        return;
      case 'addReference':
        await this.addReference();
        return;
      case 'declareReference':
        await this.declareReference(msg.logicalName);
        return;
      case 'deleteReference':
        await this.deleteReference(msg.logicalName);
        return;
      case 'openUsage':
        await this.openUsage(msg.filePath);
        return;
    }
  }

  private async loadWorkflowStatus(): Promise<WorkflowStatusView[]> {
    try {
      return await listWorkflowStatus(this.workspace);
    } catch (workflowError) {
      const message = (workflowError as Error).message ?? 'Failed to load workflow status.';
      logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to load workflow status: <pii>${message}</pii>`);
      return [];
    }
  }

  private async loadAndPost(): Promise<void> {
    this.post({ type: 'busy', busy: true, message: 'Loading connections…' });
    try {
      const views = await listAgentConnections(this.workspace);
      this.currentViews = views;
      const workflows = await this.loadWorkflowStatus();
      this.post({ type: 'data', views, workflows });
    } catch (error) {
      this.reportError(error, 'Failed to load connections.', 'Failed to load agent connections');
    }
  }

  private async enableWorkflow(workflowId: string, activate: boolean): Promise<void> {
    this.post({ type: 'busy', busy: true, message: activate ? 'Enabling workflow…' : 'Disabling workflow…' });
    try {
      const result = await setWorkflowStates(this.workspace, [{ workflowId, activate }]);
      const updated = result.workflows.find(w => w.workflowId === workflowId);
      const isActivated = updated?.state === WorkflowState.Activated;
      if (!result.succeeded || (activate && !isActivated)) {
        await this.loadAndPost();
        this.post({ type: 'warning', message: result.message ?? 'The workflow could not be enabled and was kept as a draft.' });
        return;
      }
      void vscode.window.showInformationMessage(activate ? 'Workflow enabled.' : 'Workflow disabled.');
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to update the workflow.', 'Failed to set workflow state');
    }
  }

  private async addReference(): Promise<void> {
    const usedConnectorIds = new Set(
      this.currentViews.map(v => v.connectorName).filter((name): name is string => !!name)
    );
    const connectorInternalId = await pickConnector(this.workspace, usedConnectorIds);
    if (!connectorInternalId) {
      return;
    }
    this.post({ type: 'busy', busy: true, message: 'Creating connection reference…' });
    try {
      const result = await createConnectionReference(this.workspace, connectorInternalId);
      void vscode.window.showInformationMessage(`Connection reference '${result.logicalName}' created.`);
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to create the connection reference.', 'Failed to create connection reference');
    }
  }

  private async declareReference(logicalName: string): Promise<void> {
    const trimmed = logicalName.trim();
    if (!trimmed) {
      return;
    }
    this.post({ type: 'busy', busy: true, message: 'Declaring connection reference…' });
    try {
      const result = await declareConnectionReferences(this.workspace, [trimmed]);
      if (result.invalid.length > 0) {
        this.post({ type: 'error', message: `Couldn't declare connection reference '${trimmed}'. A connector could not be determined from the name.` });
        await this.loadAndPost();
        return;
      }
      void vscode.window.showInformationMessage(`Connection reference '${trimmed}' declared.`);
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to declare the connection reference.', 'Failed to declare connection reference');
    }
  }

  private async deleteReference(logicalName: string): Promise<void> {
    this.post({ type: 'busy', busy: true, message: 'Removing connection reference…' });
    try {
      const first = await removeConnectionReference(this.workspace, logicalName, false);
      if (first.removed) {
        void vscode.window.showInformationMessage(`Connection reference '${logicalName}' removed.`);
        await this.loadAndPost();
        return;
      }
      if (first.usages.length === 0) {
        this.post({ type: 'error', message: first.message ?? 'Failed to remove the connection reference.' });
        return;
      }
      const confirmed = await this.confirmReferenceRemoval(logicalName, first.usages);
      if (!confirmed) {
        await this.loadAndPost();
        return;
      }
      this.post({ type: 'busy', busy: true, message: 'Removing connection reference…' });
      const second = await removeConnectionReference(this.workspace, logicalName, true);
      if (second.removed) {
        void vscode.window.showInformationMessage(`Connection reference '${logicalName}' removed.`);
      } else {
        this.post({ type: 'error', message: second.message ?? 'Failed to remove the connection reference.' });
      }
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to remove the connection reference.', 'Failed to remove connection reference');
    }
  }

  private async confirmReferenceRemoval(logicalName: string, usages: ConnectionReferenceUsage[]): Promise<boolean> {
    const locations = usages.map(u => u.displayName || u.filePath).filter(Boolean);
    const preview = locations.slice(0, 5).join(', ');
    const more = locations.length > 5 ? `, and ${locations.length - 5} more` : '';
    const detail = locations.length > 0 ? `It is still used by: ${preview}${more}.` : 'It is still used in this agent.';
    const remove = 'Remove anyway';
    const choice = await vscode.window.showWarningMessage(
      `Connection reference '${logicalName}' is still in use. ${detail}`,
      { modal: true },
      remove
    );
    return choice === remove;
  }

  private async openUsage(filePath: string): Promise<void> {
    if (!filePath) {
      return;
    }
    try {
      const base = vscode.Uri.parse(this.workspace.workspaceUri);
      let target = vscode.Uri.joinPath(base, filePath);
      if (/workflow\.json$/i.test(filePath)) {
        try {
          await vscode.workspace.fs.stat(target);
        } catch {
          target = vscode.Uri.joinPath(base, filePath.replace(/workflow\.json$/i, 'metadata.yml'));
        }
      }
      await vscode.window.showTextDocument(target, { preview: true });
    } catch (error) {
      const message = (error as Error).message ?? 'Failed to open the file.';
      this.post({ type: 'error', message });
    }
  }

  private async applyBindings(bindings: ConnectionBindingRequest[], creates: CreateConnectionRequest[] = []): Promise<void> {
    if (!bindings.length && !creates.length) {
      return;
    }

    const allBindings = [...bindings];

    const createsByConnector = new Map<string, CreateConnectionRequest[]>();
    for (const create of creates) {
      const list = createsByConnector.get(create.connectorName) ?? [];
      list.push(create);
      createsByConnector.set(create.connectorName, list);
    }

    for (const [connectorName, requests] of createsByConnector) {
      const created = await this.createConnection(requests[0].connectionReferenceLogicalName, connectorName);
      if (!created) {
        continue;
      }
      for (const request of requests) {
        allBindings.push({
          connectionReferenceLogicalName: request.connectionReferenceLogicalName,
          connectionId: created.connectionId,
          connectionDisplayName: created.connectionDisplayName
        });
      }
    }

    if (!allBindings.length) {
      await this.loadAndPost();
      return;
    }

    this.post({ type: 'busy', busy: true, message: 'Applying connection bindings…' });
    try {
      const views = await applyConnectionBindings(this.workspace, allBindings);
      this.currentViews = views;
      void vscode.window.showInformationMessage('Connection bindings updated.');
      const workflows = await this.loadWorkflowStatus();
      this.post({ type: 'data', views, workflows });
    } catch (error) {
      this.reportError(error, 'Failed to apply connection bindings.', 'Failed to apply connection bindings');
    }
  }

  private async createConnection(connectionReferenceLogicalName: string, connectorName: string): Promise<ConnectionBindingRequest | undefined> {
    const syncInfo = this.workspace.syncInfo;
    if (!syncInfo) {
      return undefined;
    }

    const clusterCategory = syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory;

    this.post({ type: 'busy', busy: true, message: `Complete the new connection for '${connectionReferenceLogicalName}' in your browser…` });

    const tokenSource = new vscode.CancellationTokenSource();
    try {
      if (isCustomConnectorInternalId(connectorName)) {
        this.post({ type: 'busy', busy: true, message: `Waiting for the custom connector for '${connectionReferenceLogicalName}' to become ready…` });
        await waitForCustomConnectorReady(this.workspace, connectorName, { cancellationToken: tokenSource.token });
        this.post({ type: 'busy', busy: true, message: `Complete the new connection for '${connectionReferenceLogicalName}' in your browser…` });
      }

      const result = await awaitConnectionCreation({
        connectorName,
        environmentId: syncInfo.environmentId,
        clusterCategory,
        cancellationToken: tokenSource.token
      });

      if (result.status === 'cancelled') {
        this.post({ type: 'info', message: 'Connection creation was cancelled.' });
        return undefined;
      }
      if (result.status === 'error') {
        const message = result.errorMessage ?? 'Connection creation failed.';
        this.post({ type: 'error', message });
        return undefined;
      }

      const connectionId = resolveCreatedConnectionId(result);
      if (!connectionId) {
        this.post({ type: 'error', message: 'Connection was created but no identifier was returned.' });
        return undefined;
      }

      return {
        connectionReferenceLogicalName,
        connectionId,
        connectionDisplayName: result.displayName || connectorName
      };
    } finally {
      tokenSource.dispose();
    }
  }

  private reportError(error: unknown, fallback: string, logLabel: string): void {
    const message = (error as Error).message ?? fallback;
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `${logLabel}: <pii>${message}</pii>`);
    this.post({ type: 'error', message });
  }

  private post(message: unknown): void {
    void this.panel.webview.postMessage(message);
  }

  private dispose(): void {
    ConnectionManagerController.panels.delete(this.workspace.workspaceUri);
    while (this.disposables.length) {
      this.disposables.pop()?.dispose();
    }
  }
}

export interface AutoBindResult {
  boundCount: number;
  needsNewCount: number;
  enabledWorkflowCount: number;
  disabledWorkflowNames: string[];
}

export const autoBindAgentConnections = async (workspace: CopilotStudioWorkspace, enableEligibleWorkflows = false): Promise<AutoBindResult> => {
  let views: AgentConnectionView[];
  try {
    views = await listAgentConnections(workspace);
  } catch (error) {
    const message = (error as Error).message ?? 'Failed to list agent connections.';
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to auto-bind connections: <pii>${message}</pii>`);
    return { boundCount: 0, needsNewCount: 0, enabledWorkflowCount: 0, disabledWorkflowNames: [] };
  }

  const bindings: ConnectionBindingRequest[] = [];
  let needsNewCount = 0;
  for (const view of views) {
    if (view.boundConnectionExists) {
      continue;
    }
    const connectedCandidates = view.candidates.filter(c => (c.status || '').toLowerCase() === 'connected');
    if (connectedCandidates.length !== 1) {
      needsNewCount++;
      continue;
    }
    const candidate = connectedCandidates[0];
    bindings.push({
      connectionReferenceLogicalName: view.connectionReferenceLogicalName,
      connectionId: candidate.name,
      connectionDisplayName: candidate.displayName || candidate.name
    });
  }

  if (bindings.length) {
    try {
      await applyConnectionBindings(workspace, bindings);
    } catch (error) {
      const message = (error as Error).message ?? 'Failed to bind existing connections.';
      logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to auto-bind connections: <pii>${message}</pii>`);
      return { boundCount: 0, needsNewCount, enabledWorkflowCount: 0, disabledWorkflowNames: [] };
    }
  }

  const { enabledCount, disabledWorkflowNames } = enableEligibleWorkflows
    ? await enableEligibleDraftWorkflows(workspace)
    : { enabledCount: 0, disabledWorkflowNames: await listDisabledWorkflowNames(workspace) };

  return {
    boundCount: bindings.length,
    needsNewCount,
    enabledWorkflowCount: enabledCount,
    disabledWorkflowNames
  };
};

const listDisabledWorkflowNames = async (workspace: CopilotStudioWorkspace): Promise<string[]> => {
  let workflows: WorkflowStatusView[];
  try {
    workflows = await listWorkflowStatus(workspace);
  } catch (error) {
    const message = (error as Error).message ?? 'Failed to list workflow status.';
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to list workflow status: <pii>${message}</pii>`);
    return [];
  }

  return workflows
    .filter(workflow => workflow.state !== WorkflowState.Activated)
    .map(workflow => workflow.displayName);
};

const enableEligibleDraftWorkflows = async (workspace: CopilotStudioWorkspace): Promise<{ enabledCount: number; disabledWorkflowNames: string[] }> => {
  let workflows: WorkflowStatusView[];
  try {
    workflows = await listWorkflowStatus(workspace);
  } catch (error) {
    const message = (error as Error).message ?? 'Failed to list workflow status.';
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to enable workflows: <pii>${message}</pii>`);
    return { enabledCount: 0, disabledWorkflowNames: [] };
  }

  const toEnable = workflows.filter(w => w.canEnable && w.state !== WorkflowState.Activated);
  const disabledWorkflowNames = workflows
    .filter(w => !w.canEnable && w.state !== WorkflowState.Activated)
    .map(w => w.displayName);

  if (toEnable.length === 0) {
    return { enabledCount: 0, disabledWorkflowNames };
  }

  let refreshed: WorkflowStatusView[];
  try {
    const result = await setWorkflowStates(workspace, toEnable.map(w => ({ workflowId: w.workflowId, activate: true })));
    refreshed = result.workflows;
  } catch (error) {
    const message = (error as Error).message ?? 'Failed to enable the workflows.';
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to enable workflows: <pii>${message}</pii>`);
    return { enabledCount: 0, disabledWorkflowNames: [...disabledWorkflowNames, ...toEnable.map(w => w.displayName)] };
  }

  const refreshedById = new Map(refreshed.map(w => [w.workflowId, w]));
  let enabledCount = 0;
  for (const workflow of toEnable) {
    const updated = refreshedById.get(workflow.workflowId);
    if (updated && updated.state === WorkflowState.Activated) {
      enabledCount++;
    } else {
      disabledWorkflowNames.push(workflow.displayName);
    }
  }

  return { enabledCount, disabledWorkflowNames };
};

export const promptManageConnections = async (context: vscode.ExtensionContext, workspace: CopilotStudioWorkspace): Promise<void> => {
  const manageNow = 'Manage now';
  const choice = await vscode.window.showInformationMessage(
    'This agent has connections that still need to be set up before it can run.',
    { modal: true },
    manageNow
  );
  if (choice === manageNow) {
    await ConnectionManagerController.show(context, workspace);
  }
};

