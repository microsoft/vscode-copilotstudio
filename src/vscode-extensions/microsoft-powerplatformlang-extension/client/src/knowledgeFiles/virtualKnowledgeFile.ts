import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces, tryRepairAgentManagementEndpoint } from '../sync/localWorkspaces';
import { knowledgeTreeDataProvider } from './knowledgeFileTree';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import {
  DownloadKnowledgeFilesRequest,
  DownloadKnowledgeFilesResponse,
  ListKnowledgeFilesRequest,
  ListKnowledgeFilesResponse
} from '../types';

let virtualKnowledgeProvider: virtualKnowledgeFileSystemProvider | undefined;
let virtualTreeProvider: knowledgeTreeDataProvider | undefined;
let commandsRegistered = false;

function isTextFile(filename: string): boolean {
  const textExt = new Set(['.txt', '.json', '.yaml', '.yml', '.js', '.ts', '.md']);
  return textExt.has(path.extname(filename).toLowerCase());
}

function displayLabel(ws: CopilotStudioWorkspace, relativePath: string, fileName: string): string {
  const parts = relativePath.split('/');
  const agentName = ws.displayName || path.basename(vscode.Uri.parse(ws.workspaceUri).fsPath);
  const agentIndex = parts.indexOf('agents');
  if (agentIndex >= 0 && parts.length > agentIndex + 1) {
    return `${fileName} (${agentName}/${parts[agentIndex + 1]})`;
  }
  return `${fileName} (${agentName})`;
}

function componentKey(ws: CopilotStudioWorkspace, relativePath: string, schema: string): string {
  const workspaceFsPath = vscode.Uri.parse(ws.workspaceUri).fsPath;
  return `${workspaceFsPath}|${relativePath}|${schema}`;
}

export async function registerVirtualKnowledgeProvider(context: vscode.ExtensionContext, workspace: CopilotStudioWorkspace): Promise<virtualKnowledgeFileSystemProvider> {
  if (!virtualKnowledgeProvider) {
    virtualKnowledgeProvider = new virtualKnowledgeFileSystemProvider();
    const disposable = vscode.workspace.registerFileSystemProvider('virtualKnowledge', virtualKnowledgeProvider, { isReadonly: true, isCaseSensitive: true });
    context.subscriptions.push(disposable, virtualKnowledgeProvider);
  }
  virtualKnowledgeProvider.addWorkspace(workspace);
  return virtualKnowledgeProvider;
}

export async function initializeVirtualKnowledgeTree(context: vscode.ExtensionContext) {
  if (!commandsRegistered) {
    commandsRegistered = true;
    context.subscriptions.push(
      vscode.commands.registerCommand('microsoft-copilot-studio.refreshKnowledgeFilesTreeView', async () => {
        await vscode.window.withProgress({
          location: { viewId: 'virtual-knowledge-files' },
          title: 'Refreshing remote knowledge files...',
        }, async () => {
          if (virtualKnowledgeProvider) {
            await virtualKnowledgeProvider.refresh();
          }
          virtualTreeProvider?.refresh();
        });
      }),
      vscode.commands.registerCommand('microsoft-copilot-studio.downloadAllKnowledgeFiles', async () => {
        await vscode.window.withProgress({
          location: { viewId: 'virtual-knowledge-files' },
          title: 'Downloading all remote knowledge files...',
        }, async () => {
          if (virtualKnowledgeProvider) {
            await virtualKnowledgeProvider.downloadAll();
          }
        });
      })
    );
  }

  const tryInitializeVirtualKnowledgeTree = async () => {
    const allWorkspaces = getAllWorkspaces();
    if (allWorkspaces.length === 0) {
      return;
    }

    for (const ws of allWorkspaces) {
      await registerVirtualKnowledgeProvider(context, ws);
    }

    if (!virtualTreeProvider && virtualKnowledgeProvider) {
      virtualTreeProvider = new knowledgeTreeDataProvider(virtualKnowledgeProvider);
      vscode.window.registerTreeDataProvider('virtual-knowledge-files', virtualTreeProvider);
    }

    if (virtualKnowledgeProvider) {
      await virtualKnowledgeProvider.refresh();
    }
    virtualTreeProvider?.refresh();
  };

  // try immediately, in case workspaces already loaded
  await tryInitializeVirtualKnowledgeTree();

  // call tryInitializeVirtualKnowledgeTree() when workspace available
  addWorkspaceChangeSubscription(() => {
    tryInitializeVirtualKnowledgeTree();
  });
}

interface VirtualKnowledgeComponent {
  schema: string;
  fileName: string;
  relativePath: string;
  ws: CopilotStudioWorkspace;
  label: string;
}

interface VirtualKnowledgeEntry {
  uri: vscode.Uri;
  label: string;
}

export class virtualKnowledgeFileSystemProvider implements vscode.FileSystemProvider {
  private workspaces: CopilotStudioWorkspace[] = [];
  private components = new Map<string, VirtualKnowledgeComponent>();
  private _onDidChangeFile = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  private _shutdownCts = new vscode.CancellationTokenSource();
  readonly onDidChangeFile = this._onDidChangeFile.event;

  addWorkspace(ws: CopilotStudioWorkspace): void {
    const key = ws.workspaceUri.toString();
    if (!this.workspaces.some(w => w.workspaceUri.toString() === key)) {
      this.workspaces.push(ws);
    }
  }

  // No-op watch implementation and set _onDidChangeFile.fire() to prevent vs code polling.
  // vs code thinks that we have our own watch and will not poll every 5 seconds.
  watch(uri: vscode.Uri, options: { recursive: boolean; excludes: string[] }): vscode.Disposable {
      return { dispose: () => {} };
  }

  async refresh(): Promise<void> {
    this.components.clear();
    for (const ws of this.workspaces) {
      try {
        await this.refreshWorkspace(ws);
      } catch (err) {
        logger.error('KnowledgeFiles', `Failed to refresh workspace ${ws.workspaceUri.toString()}: ${err}`);
        logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `Failed to refresh workspace <pii>${ws.workspaceUri.toString()}</pii>: ${err}`);
      }
    }

    this._onDidChangeFile.fire([
      {
        type: vscode.FileChangeType.Changed,
        uri: vscode.Uri.parse('virtualKnowledge:/'),
      },
    ]);
  }

  async downloadAll(): Promise<void> {
    logger.info('KnowledgeFiles', `Downloading knowledge files for ${this.workspaces.length} workspace(s)`);
    for (const ws of this.workspaces) {
      try {
        await this.downloadWorkspaceFiles(ws);
      } catch (err) {
        logger.error('KnowledgeFiles', `Failed to download knowledge files for ${ws.workspaceUri.toString()}: ${err}`);
        logger.logError(TelemetryEventsKeys.DownloadKnowledgeFileError, `Failed to download knowledge files for <pii>${ws.workspaceUri.toString()}</pii>: ${err}`);
      }
    }
  }

  private async refreshWorkspace(ws: CopilotStudioWorkspace): Promise<void> {
    const { syncInfo, workspaceUri } = ws;
    if (!syncInfo || !syncInfo.dataverseEndpoint || !syncInfo.agentId) {
      return;
    }

    if (!syncInfo.agentManagementEndpoint) {
      await tryRepairAgentManagementEndpoint(syncInfo, workspaceUri);
    }

    const request: ListKnowledgeFilesRequest = {
      ...(await buildLspRequestPayload(syncInfo)),
      workspaceUri
    };
    const result = await lspClient.sendRequest<ListKnowledgeFilesResponse>(LspMethods.LIST_KNOWLEDGE_FILES, request);

    for (const file of result.files) {
      const key = componentKey(ws, file.relativePath, file.schemaName);
      this.components.set(key, {
        schema: file.schemaName,
        fileName: file.fileName,
        relativePath: file.relativePath,
        ws,
        label: displayLabel(ws, file.relativePath, file.fileName)
      });
    }
  }

  private async downloadWorkspaceFiles(ws: CopilotStudioWorkspace, schemaNames?: string[], token?: vscode.CancellationToken): Promise<void> {
    const { syncInfo, workspaceUri } = ws;
    if (!syncInfo || !syncInfo.dataverseEndpoint) {
      return;
    }

    if (!syncInfo.agentManagementEndpoint) {
      await tryRepairAgentManagementEndpoint(syncInfo, workspaceUri);
    }

    const request: DownloadKnowledgeFilesRequest = {
      ...(await buildLspRequestPayload(syncInfo)),
      workspaceUri,
      schemaNames
    };
    await lspClient.sendRequest<DownloadKnowledgeFilesResponse>(LspMethods.DOWNLOAD_KNOWLEDGE_FILES, request, token);
  }

  dispose(): void {
    this._shutdownCts.cancel();
    this._shutdownCts.dispose();
    this._onDidChangeFile.dispose();
  }

  private resolveComponent(uri: vscode.Uri): { key: string; component: VirtualKnowledgeComponent } | undefined {
    const raw = uri.path.slice(1);
    const rawComponent = this.components.get(raw);
    if (rawComponent) {
      return { key: raw, component: rawComponent };
    }

    let decoded: string;
    try {
      decoded = decodeURIComponent(raw);
    } catch {
      return undefined;
    }

    const decodedComponent = this.components.get(decoded);
    return decodedComponent ? { key: decoded, component: decodedComponent } : undefined;
  }

  async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
    if (uri.path === '/') {
      return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };
    }

    const resolved = this.resolveComponent(uri);
    if (!resolved) {
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `File not found in components map: <pii>${uri.path.slice(1)}</pii>`);
      throw vscode.FileSystemError.FileNotFound();
    }
    return { type: vscode.FileType.File, ctime: 0, mtime: 0, size: 0 };
  }

  async readDirectory(uri: vscode.Uri): Promise<[string, vscode.FileType][]> {
    if (uri.path !== '/') {
      throw vscode.FileSystemError.FileNotFound();
    }

    // Return encoded names so stat/readFile can reliably decode back to the internal key.
    return Array.from(this.components.keys()).map(key => [encodeURIComponent(key), vscode.FileType.File]);
  }

  getEntries(): VirtualKnowledgeEntry[] {
    return Array.from(this.components.entries()).map(([key, component]) => ({
      uri: vscode.Uri.parse(`virtualKnowledge:/${encodeURIComponent(key)}`),
      label: component.label
    }));
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const resolved = this.resolveComponent(uri);
    if (!resolved) {
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `File not found: <pii>${uri.path.slice(1)}</pii>`);
      throw vscode.FileSystemError.FileNotFound();
    }

    const { component } = resolved;
    const { workspaceUri } = component.ws;
    const localPath = path.join(vscode.Uri.parse(workspaceUri).fsPath, component.relativePath);

    try {
      await this.downloadWorkspaceFiles(component.ws, [component.schema], this._shutdownCts.token);
    } catch (err) {
      logger.error('KnowledgeFiles', `Failed to download ${component.fileName}: ${err}`);
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `Failed to download <pii>${component.fileName}</pii>: ${err}`);
      throw vscode.FileSystemError.Unavailable();
    }

    const content = await fs.readFile(localPath);

    if (isTextFile(component.fileName)) {
      const doc = await vscode.workspace.openTextDocument(localPath);
      await vscode.window.showTextDocument(doc, { preview: false });
    }

    return content;
  }

  writeFile() {
    throw vscode.FileSystemError.NoPermissions();
  }
  delete() {
    throw vscode.FileSystemError.NoPermissions();
  }
  createDirectory() {
    throw vscode.FileSystemError.NoPermissions();
  }
  rename() {
    throw vscode.FileSystemError.NoPermissions();
  }
}
