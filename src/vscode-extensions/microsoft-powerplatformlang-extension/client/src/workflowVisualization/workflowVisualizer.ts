import * as vscode from 'vscode';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';
import { FOCUS_NODE_COMMAND } from '../workflows/workflowCommands';
import type { FocusNodeArgs } from '../workflows/workflowCommands';
import { buildGraphModel } from './workflowGraphModel';
import { buildWebviewHtml } from './webviewHtml';
import { listConnectors } from '../connections/connectionCatalog';
import { addWorkspaceChangeSubscription, getWorkspaceByUri } from '../sync/localWorkspaces';
import type { CopilotStudioWorkspace } from '../sync/localWorkspaces';
import type { ConnectorInfo } from '../types';

const REFRESH_DEBOUNCE_MS = 350;
const CATALOG_RETRY_DELAY_MS = 30_000;

interface RevealMessage {
  type: 'reveal';
  nodeId: string;
  offset?: number;
  length?: number;
}

interface ReadyMessage {
  type: 'ready';
}

type WebviewMessage = RevealMessage | ReadyMessage;

export class WorkflowVisualizerController {
  private static readonly panels = new Map<string, WorkflowVisualizerController>();

  public static show(context: vscode.ExtensionContext, document: vscode.TextDocument): void {
    const key = document.uri.toString();
    const existing = WorkflowVisualizerController.panels.get(key);
    if (existing) {
      existing.panel.reveal(vscode.ViewColumn.Beside);
      return;
    }
    const controller = new WorkflowVisualizerController(context, document);
    WorkflowVisualizerController.panels.set(key, controller);
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];
  private refreshTimer: NodeJS.Timeout | undefined;
  private connectors: ConnectorInfo[] = [];
  private catalogRequested = false;
  private catalogKey: string | undefined;
  private catalogRetryAt = 0;
  private readonly warnedCatalogKeys = new Set<string | undefined>();
  private disposed = false;

  private constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly document: vscode.TextDocument,
  ) {
    this.panel = vscode.window.createWebviewPanel(
      'copilotStudioWorkflowVisualizer',
      `Visualize: ${this.shortName(document.uri)}`,
      vscode.ViewColumn.Beside,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [context.extensionUri],
      },
    );

    this.panel.webview.html = buildWebviewHtml(this.panel.webview, context.extensionUri);

    this.disposables.push(
      this.panel.webview.onDidReceiveMessage((msg: WebviewMessage) => this.onMessage(msg)),
      addWorkspaceChangeSubscription(() => this.scheduleRefresh()),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (e.document.uri.toString() === this.document.uri.toString()) {
          this.scheduleRefresh();
        }
      }),
      vscode.window.onDidChangeTextEditorSelection((e) => {
        if (e.textEditor.document.uri.toString() === this.document.uri.toString()) {
          this.syncSelectionToWebview(e.textEditor.selection.active);
        }
      }),
    );

    this.panel.onDidDispose(() => this.dispose(), undefined, this.disposables);
  }

  private shortName(uri: vscode.Uri): string {
    const parts = uri.path.split('/');
    const dir = parts.length >= 2 ? parts[parts.length - 2] : '';
    return dir || parts[parts.length - 1];
  }

  private onMessage(msg: WebviewMessage): void {
    if (msg.type === 'ready') {
      this.postModel(true);
      return;
    }
    if (msg.type === 'reveal') {
      if (typeof msg.offset === 'number' && typeof msg.length === 'number') {
        const args: FocusNodeArgs = {
          uri: this.document.uri.toString(),
          offset: msg.offset,
          length: msg.length,
        };
        void vscode.commands.executeCommand(FOCUS_NODE_COMMAND, args);
      }
    }
  }

  private scheduleRefresh(): void {
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
    }
    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = undefined;
      this.postModel();
    }, REFRESH_DEBOUNCE_MS);
  }

  private postModel(fit = false): void {
    try {
      const workspace = getWorkspaceByUri(this.document.uri);
      const catalogKey = workspace?.syncInfo ? JSON.stringify([
        workspace.workspaceUri, workspace.syncInfo.dataverseEndpoint,
        workspace.syncInfo.environmentId, workspace.syncInfo.accountInfo,
      ]) : undefined;
      if (catalogKey !== this.catalogKey) {
        this.catalogKey = catalogKey;
        this.connectors = [];
        this.catalogRequested = false;
        this.catalogRetryAt = 0;
      }
      const model = buildGraphModel(this.document.getText(), this.connectors);
      void this.panel.webview.postMessage({ type: 'model', model, fit });
      if (workspace?.syncInfo && !this.catalogRequested && Date.now() >= this.catalogRetryAt
        && model.nodes.some(node => node.connectorId)) {
        this.catalogRequested = true;
        void this.loadConnectorIcons(workspace, catalogKey);
      }
    } catch (error) {
      logger.logError(
        TelemetryEventsKeys.WorkflowVisualizeError,
        'Failed to build workflow graph model',
        { error },
      );
    }
  }

  private async loadConnectorIcons(workspace: CopilotStudioWorkspace, catalogKey: string | undefined): Promise<void> {
    try {
      const connectors = await listConnectors(workspace);
      if (!this.disposed && this.catalogKey === catalogKey) {
        this.connectors = connectors;
        this.catalogRetryAt = 0;
        this.postModel();
      }
    } catch (error) {
      logger.logError(
        TelemetryEventsKeys.WorkflowVisualizeError,
        undefined,
        { message: 'Failed to load workflow connector icons', error },
      );
      if (!this.disposed && this.catalogKey === catalogKey) {
        this.catalogRequested = false;
        this.catalogRetryAt = Date.now() + CATALOG_RETRY_DELAY_MS;
        if (!this.warnedCatalogKeys.has(catalogKey)) {
          this.warnedCatalogKeys.add(catalogKey);
          void vscode.window.showWarningMessage('Could not load the connector catalog. Connector catalog icons are unavailable.');
        }
      }
    }
  }

  private syncSelectionToWebview(position: vscode.Position): void {
    const offset = this.document.offsetAt(position);
    void this.panel.webview.postMessage({ type: 'selection', offset });
  }

  private dispose(): void {
    this.disposed = true;
    WorkflowVisualizerController.panels.delete(this.document.uri.toString());
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
    }
    while (this.disposables.length) {
      this.disposables.pop()?.dispose();
    }
  }
}
