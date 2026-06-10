import * as vscode from 'vscode';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';
import { FOCUS_NODE_COMMAND } from '../workflows/workflowCommands';
import type { FocusNodeArgs } from '../workflows/workflowCommands';
import { buildGraphModel } from './workflowGraphModel';
import { buildWebviewHtml } from './webviewHtml';

const REFRESH_DEBOUNCE_MS = 350;

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
      this.postModel();
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

  private postModel(): void {
    try {
      const model = buildGraphModel(this.document.getText());
      void this.panel.webview.postMessage({ type: 'model', model });
    } catch (error) {
      logger.logError(TelemetryEventsKeys.WorkflowVisualizeError, `Failed to build workflow graph model: <pii>${error}</pii>`);
    }
  }

  private syncSelectionToWebview(position: vscode.Position): void {
    const offset = this.document.offsetAt(position);
    void this.panel.webview.postMessage({ type: 'selection', offset });
  }

  private dispose(): void {
    WorkflowVisualizerController.panels.delete(this.document.uri.toString());
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
    }
    while (this.disposables.length) {
      this.disposables.pop()?.dispose();
    }
  }
}
