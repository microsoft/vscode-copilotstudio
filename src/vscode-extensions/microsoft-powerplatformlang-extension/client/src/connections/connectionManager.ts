import * as vscode from 'vscode';
import logger from '../services/logger';
import { HttpRequestError } from '../utils/httpRequestError';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { CopilotStudioWorkspace, getWorkspaceByUri, getWorkspaceKindLabel, tryRepairAgentManagementEndpoint } from '../sync/localWorkspaces';
import { AgentSyncInfo, ConnectionBindingRequest, ConnectionReferenceUsage, AgentConnectionView, WorkflowState, WorkflowStatusView } from '../types';
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
import { DeploymentContext, loadDeploymentContext, resolveEnvironmentNames } from '../deployments/deploymentContext';
import { findEnvironmentEndpointInListAsync, getEnvironmentDisplayNameByIdAsync, getEnvironmentEndpointByIdAsync } from '../clients/bapClient';
import { resolveAccountIdentity } from '../clients/account';
import { getSolutionVersionsAsync } from '../clients/dataverseClient';

interface ReadyMessage { type: 'ready'; }
interface RefreshMessage { type: 'refresh'; hasPendingChanges: boolean; }
interface RefreshDeploymentsMessage { type: 'refreshDeployments'; hasPendingChanges: boolean; }
interface CreateConnectionRequest { connectionReferenceLogicalName: string; connectorName: string; }
interface ApplyMessage { type: 'apply'; bindings: ConnectionBindingRequest[]; creates: CreateConnectionRequest[]; }
interface EnableWorkflowMessage { type: 'enableWorkflow'; workflowId: string; activate: boolean; }
interface AddReferenceMessage { type: 'addReference'; }
interface DeclareReferenceMessage { type: 'declareReference'; logicalName: string; }
interface DeleteReferenceMessage { type: 'deleteReference'; logicalName: string; }
interface OpenUsageMessage { type: 'openUsage'; filePath: string; }
interface SelectDeploymentMessage { type: 'selectDeployment'; deploymentId: string; hasPendingChanges: boolean; }

type WebviewMessage =
  | ReadyMessage
  | RefreshMessage
  | RefreshDeploymentsMessage
  | ApplyMessage
  | EnableWorkflowMessage
  | AddReferenceMessage
  | DeclareReferenceMessage
  | DeleteReferenceMessage
  | OpenUsageMessage
  | SelectDeploymentMessage;

const workspaceAttachmentChangedMessage = 'The workspace attachment changed. Refresh connections before making changes.';

export class ConnectionManagerController {
  private static readonly panels = new Map<string, ConnectionManagerController>();

  public static async show(context: vscode.ExtensionContext, workspace: CopilotStudioWorkspace): Promise<void> {
    const key = workspace.workspaceUri;
    const existing = ConnectionManagerController.panels.get(key);
    if (existing) {
      existing.updateWorkspace(workspace);
      existing.panel.reveal(vscode.ViewColumn.Active);
      return;
    }
    const controller = new ConnectionManagerController(context, workspace, buildConnectionManagerHtml);
    ConnectionManagerController.panels.set(key, controller);
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];
  private currentViews: AgentConnectionView[] = [];
  private deploymentContext?: DeploymentContext;
  private selectedDeploymentId?: string;
  private connectionWorkspace?: CopilotStudioWorkspace;
  private connectionLoadError?: string;
  private connectionTargetChanged = false;
  private operationInProgress = false;
  private workspaceAttachment: string;
  private workspaceAttachmentChanged = false;
  private pendingWorkspace?: CopilotStudioWorkspace;

  private constructor(
    private readonly context: vscode.ExtensionContext,
    private workspace: CopilotStudioWorkspace,
    buildHtml: (webview: vscode.Webview, agentName: string) => string
  ) {
    this.workspaceAttachment = this.getWorkspaceAttachment(workspace);
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
    if (this.operationInProgress) {
      return;
    }
    this.operationInProgress = true;
    try {
      if (!['ready', 'refresh', 'refreshDeployments'].includes(msg.type)) {
        this.ensureWorkspaceAttachmentUnchanged();
      }
      if (msg.type === 'selectDeployment') {
        await this.selectDeployment(msg);
        return;
      }
      if (this.connectionLoadError !== undefined && !['ready', 'refresh', 'refreshDeployments'].includes(msg.type)) {
        this.post({ type: 'error', message: this.connectionLoadError, loadFailed: true });
        return;
      }
      if (this.isOtherDeployment && ['enableWorkflow', 'declareReference', 'deleteReference', 'openUsage'].includes(msg.type)) {
        throw new Error('Reference authoring and workflow controls are available only for the workspace target.');
      }
      switch (msg.type) {
        case 'ready':
          await this.loadAndPost(true, true);
          return;
        case 'refreshDeployments':
        case 'refresh':
          if (await this.confirmDiscardChanges(msg.hasPendingChanges, 'refresh')) {
            await this.loadAndPost(msg.type === 'refreshDeployments', true);
          }
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
    } catch (error) {
      this.reportError(error, 'Connections failed');
    } finally {
      this.operationInProgress = false;
      if (this.workspaceAttachmentChanged) {
        this.post({ type: 'error', message: workspaceAttachmentChangedMessage, loadFailed: true });
      }
    }
  }

  private getWorkspaceAttachment(workspace: CopilotStudioWorkspace): string {
    const syncInfo = workspace.syncInfo;
    const account = syncInfo?.accountInfo;
    return JSON.stringify([
      workspace.type, workspace.schemaName?.toLowerCase(),
      syncInfo?.environmentId.toLowerCase(), syncInfo?.dataverseEndpoint.toLowerCase(),
      syncInfo?.agentId?.toLowerCase(), syncInfo?.componentCollectionId?.toLowerCase(),
      account?.tenantId.toLowerCase(), account?.clusterCategory ?? DefaultCoreServicesClusterCategory,
      account?.accountId?.toLowerCase(), account?.accountEmail?.toLowerCase()
    ]);
  }

  private get latestWorkspace(): CopilotStudioWorkspace {
    return this.pendingWorkspace ?? getWorkspaceByUri(vscode.Uri.parse(this.workspace.workspaceUri)) ?? this.workspace;
  }

  private updateWorkspace(workspace: CopilotStudioWorkspace): void {
    if (this.workspaceAttachmentChanged || this.getWorkspaceAttachment(workspace) !== this.workspaceAttachment) {
      this.pendingWorkspace = workspace;
      this.workspaceAttachmentChanged = true;
      if (!this.operationInProgress) {
        this.reportError(new Error(workspaceAttachmentChangedMessage), 'Workspace attachment changed', true);
      }
    }
  }

  private ensureWorkspaceAttachmentUnchanged(): void {
    if (this.getWorkspaceAttachment(this.latestWorkspace) !== this.workspaceAttachment) {
      this.workspaceAttachmentChanged = true;
    }
    if (this.workspaceAttachmentChanged) {
      throw new Error(workspaceAttachmentChangedMessage);
    }
  }

  private get isOtherDeployment(): boolean {
    return !!this.selectedDeploymentId && this.selectedDeploymentId !== this.deploymentContext?.currentDeploymentId;
  }

  private get targetAgentSchemaName(): string | undefined {
    if (!this.isOtherDeployment) {
      return undefined;
    }
    const schemaName = this.deploymentContext?.agentSchemaName || this.workspace.schemaName;
    if (!schemaName?.trim()) {
      throw new Error("The deployment's agent schema name is missing.");
    }

    return schemaName;
  }

  private requireConnectionWorkspace(): CopilotStudioWorkspace {
    this.ensureWorkspaceAttachmentUnchanged();
    if (!this.connectionWorkspace) {
      throw new Error('Refresh connections before making changes.');
    }
    return this.connectionWorkspace;
  }

  private async validateConnectionTargetUnchanged(): Promise<boolean> {
    this.ensureWorkspaceAttachmentUnchanged();
    if (!this.connectionTargetChanged) {
      if (!this.selectedDeploymentId) {
        return true;
      }

      const syncInfo = this.workspace.syncInfo;
      if (!syncInfo) {
        throw new Error('Connection settings are unavailable for the selected target.');
      }

      const latest = await this.readDeploymentContext(syncInfo);
      this.ensureWorkspaceAttachmentUnchanged();
      const selected = latest.deployments.find(deployment => deployment.id === this.selectedDeploymentId);
      const loaded = this.deploymentContext?.deployments.find(deployment => deployment.id === this.selectedDeploymentId);
      if (selected && loaded
        && selected.environmentId.toLowerCase() === loaded.environmentId.toLowerCase()
        && (latest.agentSchemaName || this.workspace.schemaName || '').toLowerCase()
          === (this.deploymentContext?.agentSchemaName || this.workspace.schemaName || '').toLowerCase()) {
        return true;
      }
      this.connectionTargetChanged = true;
    }

    const error = new Error('The selected deployment was not found. Refresh deployments and try again.');
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, undefined, { message: 'Connection target changed', error });
    void vscode.window.showErrorMessage(error.message);
    this.post({ type: 'busy', busy: false });
    return false;
  }

  private async selectDeployment(msg: SelectDeploymentMessage): Promise<void> {
    if (msg.deploymentId) {
      const deployment = this.deploymentContext?.deployments.find(item => item.id === msg.deploymentId);
      if (!deployment) {
        this.post({ type: 'selectionCancelled' });
        void vscode.window.showErrorMessage('This deployment is no longer applicable to the agent. Refresh deployments and try again.');
        return;
      }
    }
    if (!await this.confirmDiscardChanges(msg.hasPendingChanges, 'switch')) {
      return;
    }
    this.ensureWorkspaceAttachmentUnchanged();
    this.selectedDeploymentId = msg.deploymentId;
    await this.loadAndPost();
  }

  private async confirmDiscardChanges(hasPendingChanges: boolean, action: 'switch' | 'refresh'): Promise<boolean> {
    if (!hasPendingChanges) {
      return true;
    }
    const discard: vscode.MessageItem = { title: `Discard changes and ${action}` };
    const detail = action === 'switch'
      ? 'Switching deployment discards the changes'
      : 'Refreshing reloads the connections and discards the changes.';
    const choice = await vscode.window.showWarningMessage(
      'You have unsaved connection changes.',
      { modal: true, detail },
      discard,
      { title: 'Cancel', isCloseAffordance: true }
    );
    if (choice !== discard) {
      this.post({ type: 'selectionCancelled' });
      return false;
    }
    return true;
  }

  private async resolveConnectionWorkspace(): Promise<CopilotStudioWorkspace> {
    const syncInfo = this.workspace.syncInfo;
    if (!syncInfo) {
      throw new Error('Connection settings are unavailable for the selected target.');
    }

    if (!this.isOtherDeployment) {
      if (!syncInfo.agentManagementEndpoint) {
        const repaired = await tryRepairAgentManagementEndpoint(syncInfo, this.workspace.workspaceUri);
        if (!repaired || !syncInfo.agentManagementEndpoint) {
          throw new Error('The workspace Copilot Studio endpoint is unavailable. Refresh the workspace and try again.');
        }
      }
      return this.workspace;
    }

    const deployment = this.deploymentContext?.deployments.find(item => item.id === this.selectedDeploymentId);
    if (!deployment) {
      throw new Error('The selected deployment was not found. Refresh deployments and try again.');
    }

    const identity = resolveAccountIdentity(syncInfo.accountInfo);
    const clusterCategory = syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory;
    let environment = await getEnvironmentEndpointByIdAsync(
      clusterCategory,
      deployment.environmentId, AbortSignal.timeout(30_000), identity.accountId ?? null, identity.accountEmail
    );
    if (!environment?.dataverseUrl || !environment.agentManagementUrl) {
      environment = await findEnvironmentEndpointInListAsync(
        clusterCategory,
        deployment.environmentId, AbortSignal.timeout(30_000), identity.accountId ?? null, identity.accountEmail
      );
    }
    if (!environment?.dataverseUrl || !environment.agentManagementUrl) {
      throw new Error('The selected environment does not have Copilot Studio enabled.');
    }

    const solutionVersions = await getSolutionVersionsAsync(
      vscode.Uri.parse(environment.dataverseUrl), AbortSignal.timeout(30_000), identity.accountId, identity.accountEmail, true
    );

    return {
      ...this.workspace,
      syncInfo: {
        ...syncInfo,
        agentId: undefined,
        componentCollectionId: undefined,
        environmentId: deployment.environmentId,
        environmentDisplayName: deployment.environmentName,
        dataverseEndpoint: environment.dataverseUrl,
        agentManagementEndpoint: environment.agentManagementUrl,
        solutionVersions
      }
    };
  }

  private async loadWorkflowStatus(): Promise<WorkflowStatusView[]> {
    if (this.isOtherDeployment) {
      return [];
    }
    try {
      return await listWorkflowStatus(this.workspace);
    } catch (workflowError) {
      logger.logError(TelemetryEventsKeys.ConnectionCreationError, 'Failed to load workflow status', { error: workflowError });
      return [];
    }
  }

  private async loadAndPost(loadingDeployments = false, acceptWorkspaceChange = false): Promise<void> {
    if (acceptWorkspaceChange) {
      const workspace = this.latestWorkspace;
      const attachment = this.getWorkspaceAttachment(workspace);
      if (this.workspaceAttachmentChanged || attachment !== this.workspaceAttachment) {
        this.selectedDeploymentId = undefined;
        this.deploymentContext = undefined;
      }
      this.workspace = workspace;
      this.pendingWorkspace = undefined;
      this.workspaceAttachment = attachment;
      this.workspaceAttachmentChanged = false;
      this.panel.title = `Connections: ${workspace.displayName}`;
    }
    this.ensureWorkspaceAttachmentUnchanged();
    this.post({ type: 'busy', busy: true, message: 'Loading connections…', loadingConnections: true, loadingDeployments });
    this.connectionWorkspace = undefined;
    this.currentViews = [];
    try {
      const selectedDeploymentRetargeted = await this.loadDeploymentContext();
      this.ensureWorkspaceAttachmentUnchanged();
      const target = await this.resolveConnectionWorkspace();
      this.ensureWorkspaceAttachmentUnchanged();
      if (!target.syncInfo) {
        throw new Error('Connection settings are unavailable for the selected target.');
      }
      const views = await listAgentConnections(target, this.targetAgentSchemaName);
      this.ensureWorkspaceAttachmentUnchanged();
      this.connectionWorkspace = target;
      this.connectionLoadError = undefined;
      this.connectionTargetChanged = false;
      this.currentViews = views;
      const workflows = await this.loadWorkflowStatus();
      this.ensureWorkspaceAttachmentUnchanged();
      this.post({ type: 'data', views, workflows });
      if (selectedDeploymentRetargeted) {
        void vscode.window.showWarningMessage(
          `The selected deployment's connection target changed. Connections have been reloaded for ${target.syncInfo.environmentDisplayName || target.syncInfo.environmentId}.`
        );
      }
    } catch (error) {
      this.connectionLoadError = this.reportError(error, 'Failed to load connections', true);
    }
  }

  private async readDeploymentContext(syncInfo: AgentSyncInfo): Promise<DeploymentContext> {
    const context = await loadDeploymentContext(vscode.Uri.parse(this.workspace.workspaceUri).fsPath, syncInfo);
    if (context.errorMessage !== undefined) {
      logger.logError(TelemetryEventsKeys.LoadDeploymentsError, undefined, {
        message: context.errorMessage
      });
    }
    if (context.matchingAgentCount !== undefined) {
      logger.logInfo(TelemetryEventsKeys.LoadDeploymentsInfo, undefined, {
        message: `Found ${context.deployments.length} deployments for the agent.`
      });
    }
    return context;
  }

  private async loadDeploymentContext(): Promise<boolean> {
    const syncInfo = this.workspace.syncInfo;
    if (!syncInfo) {
      return false;
    }
    let selectedDeploymentChange: 'removed' | 'retargeted' | undefined;
    const previous = this.deploymentContext;
    const previousSelection = previous?.deployments.find(deployment => deployment.id === this.selectedDeploymentId);
    const { errorMessage, ...deploymentContext } = await this.readDeploymentContext(syncInfo);
    if (previousSelection && errorMessage === undefined) {
      const selected = deploymentContext.deployments.find(deployment => deployment.id === previousSelection.id);
      if (!selected) {
        selectedDeploymentChange = 'removed';
      } else if (selected.environmentId.toLowerCase() !== previousSelection.environmentId.toLowerCase()
        || (deploymentContext.agentSchemaName || this.workspace.schemaName || '').toLowerCase()
          !== (previous?.agentSchemaName || this.workspace.schemaName || '').toLowerCase()) {
        selectedDeploymentChange = 'retargeted';
      }
    }
    const identity = resolveAccountIdentity(syncInfo.accountInfo);
    this.deploymentContext = await resolveEnvironmentNames(deploymentContext, async environmentId => {
      try {
        return await getEnvironmentDisplayNameByIdAsync(
          syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory,
          environmentId,
          AbortSignal.timeout(5_000),
          identity.accountId ?? null,
          identity.accountEmail
        );
      } catch (error) {
        logger.logError(TelemetryEventsKeys.ConnectionCreationError, undefined, { message: 'Failed to load deployment environment name', error });
        return undefined;
      }
    });
    this.ensureWorkspaceAttachmentUnchanged();
    const usesDeploymentSelection = this.deploymentContext.deployments.length > 1
      || this.deploymentContext.deployments.some(deployment => deployment.environmentId.toLowerCase() !== syncInfo.environmentId.toLowerCase());
    if (selectedDeploymentChange === 'removed') {
      this.selectedDeploymentId = undefined;
      void vscode.window.showErrorMessage(
        "The selected deployment is no longer available. Connections have been reloaded for the workspace's environment."
      );
    }
    if (!usesDeploymentSelection) {
      this.selectedDeploymentId = '';
    } else {
      this.selectedDeploymentId ??= this.deploymentContext.currentDeploymentId || '';
    }
    this.post({ type: 'context', context: this.deploymentContext, selectedDeploymentId: this.selectedDeploymentId, workspaceName: this.workspace.displayName });
    return selectedDeploymentChange === 'retargeted';
  }

  private async enableWorkflow(workflowId: string, activate: boolean): Promise<void> {
    this.post({ type: 'busy', busy: true, message: activate ? 'Enabling workflow…' : 'Disabling workflow…' });
    try {
      const result = await setWorkflowStates(this.workspace, [{ workflowId, activate }]);
      this.ensureWorkspaceAttachmentUnchanged();
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
      this.reportError(error, 'Failed to update workflow state');
    }
  }

  private async addReference(): Promise<void> {
    const usedConnectorIds = new Set(
      this.currentViews.map(v => v.connectorName).filter((name): name is string => !!name)
    );
    this.post({ type: 'busy', busy: true, message: 'Select a connector…' });
    const connectorInternalId = await pickConnector(this.requireConnectionWorkspace(), usedConnectorIds);
    this.ensureWorkspaceAttachmentUnchanged();
    if (!connectorInternalId) {
      this.post({ type: 'busy', busy: false });
      return;
    }
    this.post({ type: 'busy', busy: true, message: 'Creating connection reference…' });
    try {
      const result = await createConnectionReference(this.workspace, connectorInternalId);
      this.ensureWorkspaceAttachmentUnchanged();
      void vscode.window.showInformationMessage(`Connection reference '${result.logicalName}' added locally. Select a connection and Apply in each deployment to bind it there.`);
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to create connection reference');
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
      this.ensureWorkspaceAttachmentUnchanged();
      if (result.invalid.length > 0) {
        this.post({ type: 'error', message: `Couldn't declare connection reference '${trimmed}'. A connector could not be determined from the name.` });
        await this.loadAndPost();
        return;
      }
      void vscode.window.showInformationMessage(`Connection reference '${trimmed}' declared.`);
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to declare connection reference');
    }
  }

  private async deleteReference(logicalName: string): Promise<void> {
    this.post({ type: 'busy', busy: true, message: 'Removing connection reference…' });
    try {
      const first = await removeConnectionReference(this.workspace, logicalName, false);
      this.ensureWorkspaceAttachmentUnchanged();
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
      this.ensureWorkspaceAttachmentUnchanged();
      if (!confirmed) {
        await this.loadAndPost();
        return;
      }
      this.post({ type: 'busy', busy: true, message: 'Removing connection reference…' });
      const second = await removeConnectionReference(this.workspace, logicalName, true);
      this.ensureWorkspaceAttachmentUnchanged();
      if (second.removed) {
        void vscode.window.showInformationMessage(`Connection reference '${logicalName}' removed.`);
      } else {
        this.post({ type: 'error', message: second.message ?? 'Failed to remove the connection reference.' });
      }
      await this.loadAndPost();
    } catch (error) {
      this.reportError(error, 'Failed to remove connection reference');
    }
  }

  private async confirmReferenceRemoval(logicalName: string, usages: ConnectionReferenceUsage[]): Promise<boolean> {
    const locations = usages.map(u => u.displayName || u.filePath).filter(Boolean);
    const preview = locations.slice(0, 5).join(', ');
    const more = locations.length > 5 ? `, and ${locations.length - 5} more` : '';
    const detail = locations.length > 0 ? `It is still used by: ${preview}${more}.` : `It is still used in this ${getWorkspaceKindLabel(this.workspace)}.`;
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
    this.post({ type: 'busy', busy: true, message: 'Opening file…' });
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
    } finally {
      this.post({ type: 'busy', busy: false });
    }
  }

  private async applyBindings(bindings: ConnectionBindingRequest[], creates: CreateConnectionRequest[] = []): Promise<void> {
    const target = this.requireConnectionWorkspace();
    if (!await this.validateConnectionTargetUnchanged()) {
      return;
    }
    if (!bindings.length && !creates.length) {
      void vscode.window.showInformationMessage('There are no connection changes to apply.');
      this.post({ type: 'busy', busy: false });
      return;
    }
    this.post({ type: 'busy', busy: true, message: 'Applying connection bindings…' });

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

    if (createsByConnector.size > 0 && !await this.validateConnectionTargetUnchanged()) {
      return;
    }
    this.ensureWorkspaceAttachmentUnchanged();
    this.post({ type: 'busy', busy: true, message: 'Applying connection bindings…' });
    try {
      const views = await applyConnectionBindings(target, allBindings, this.targetAgentSchemaName);
      this.ensureWorkspaceAttachmentUnchanged();
      this.currentViews = views;
      void vscode.window.showInformationMessage('Connection bindings updated.');
      const workflows = await this.loadWorkflowStatus();
      this.ensureWorkspaceAttachmentUnchanged();
      this.post({ type: 'data', views, workflows });
    } catch (error) {
      this.reportError(error, 'Failed to apply connection bindings');
    }
  }

  private async createConnection(connectionReferenceLogicalName: string, connectorName: string): Promise<ConnectionBindingRequest | undefined> {
    const target = this.requireConnectionWorkspace();
    const syncInfo = target.syncInfo;
    if (!syncInfo) {
      return undefined;
    }

    const clusterCategory = syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory;

    this.post({ type: 'busy', busy: true, message: `Complete the new connection for '${connectionReferenceLogicalName}' in your browser…` });

    const tokenSource = new vscode.CancellationTokenSource();
    try {
      if (isCustomConnectorInternalId(connectorName)) {
        this.post({ type: 'busy', busy: true, message: `Waiting for the custom connector for '${connectionReferenceLogicalName}' to become ready…` });
        await waitForCustomConnectorReady(target, connectorName, { cancellationToken: tokenSource.token });
        this.ensureWorkspaceAttachmentUnchanged();
        this.post({ type: 'busy', busy: true, message: `Complete the new connection for '${connectionReferenceLogicalName}' in your browser…` });
      }

      const result = await awaitConnectionCreation({
        connectorName,
        environmentId: syncInfo.environmentId,
        clusterCategory,
        cancellationToken: tokenSource.token
      });
      this.ensureWorkspaceAttachmentUnchanged();

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

  private reportError(error: unknown, fallback: string, loadFailed = false): string {
    const message = error instanceof HttpRequestError ? error.userMessage : error instanceof Error ? error.message : fallback;
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, loadFailed ? undefined : fallback, {
      message: loadFailed ? `<pii>${message}</pii>` : fallback,
      error
    });
    this.post({ type: 'error', message, loadFailed });
    return message;
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
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, 'Failed to auto-bind connections. Listing agent connections failed', { error });
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
      logger.logError(TelemetryEventsKeys.ConnectionCreationError, 'Failed to auto-bind connections. Binding existing connections failed', { error });
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
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, 'Failed to list workflow status', { error });
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
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, 'Failed to enable workflows. Listing workflow status failed', { error });
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
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, 'Failed to enable workflows. Activating workflow states failed', { error });
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

export const promptManageConnectionsForWorkspaces = async (context: vscode.ExtensionContext, workspaces: CopilotStudioWorkspace[]): Promise<void> => {
  if (workspaces.length === 0) {
    return;
  }

  const manageNow = 'Manage now';
  const message = workspaces.length === 1
    ? `This ${getWorkspaceKindLabel(workspaces[0])} has connections that still need to be set up before it can run.`
    : `Connections still need to be set up before these can run: ${workspaces.map(workspace => `'${workspace.displayName}'`).join(', ')}. Their connection managers will open one at a time.`;
  try {
    const choice = await vscode.window.showInformationMessage(message, { modal: true }, manageNow);
    if (choice !== manageNow) {
      return;
    }

    for (const workspace of workspaces) {
      await ConnectionManagerController.show(context, workspace);
    }
  } catch (error) {
    logger.logWarning(
      TelemetryEventsKeys.ConnectionCreationError,
      `An error occurred in the connection management.`,
      { error }
    );
  }
};
