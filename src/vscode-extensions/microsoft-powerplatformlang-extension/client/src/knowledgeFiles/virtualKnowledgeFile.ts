import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces } from '../sync/localWorkspaces';
import { getDataverseBotHandler, getFilesDir, getTrackPath, safeSaveFile } from './syncUtils';
import { WsComponentMetadata } from '../botComponents/botComponentHandler';
import { ChangeTrack, isTextFile, loadChangeTrack, resolveConflict, saveChangeTrack } from './fileHelper';
import { knowledgeTreeDataProvider } from './knowledgeFileTree';
import { randId } from '../botComponents/schemaName';
import logger from '../services/logger';
import { ConflictResolution, TelemetryEventsKeys } from '../constants';
import { ChangeType } from '../types';

let virtualKnowledgeProvider: virtualKnowledgeFileSystemProvider | undefined;
let virtualTreeInitialized = false;

export async function registerVirtualKnowledgeProvider(context: vscode.ExtensionContext, workspace: CopilotStudioWorkspace): Promise<virtualKnowledgeFileSystemProvider> {
  if (!virtualKnowledgeProvider) {
    virtualKnowledgeProvider = new virtualKnowledgeFileSystemProvider(workspace);
    const disposable = vscode.workspace.registerFileSystemProvider('virtualKnowledge', virtualKnowledgeProvider, { isReadonly: true, isCaseSensitive: true });
    context.subscriptions.push(disposable);
  }
  return virtualKnowledgeProvider;
}

export async function initializeVirtualKnowledgeTree(context: vscode.ExtensionContext) {
  const tryInitializeVirtualKnowledgeTree = async () => {
    const allWorkspaces = getAllWorkspaces();
    if (allWorkspaces.length === 0 || virtualTreeInitialized) {
      return;
    }

    virtualTreeInitialized = true;

    for (const ws of allWorkspaces) {
      const virtualProvider = await registerVirtualKnowledgeProvider(context, ws);
      const provider = new knowledgeTreeDataProvider(virtualProvider);
      vscode.window.registerTreeDataProvider('virtual-knowledge-files', provider);

      await virtualProvider.refresh();
      provider.refresh();
    }
  };

  // try immediately, in case workspaces already loaded
  await tryInitializeVirtualKnowledgeTree();

  // call tryInitializeVirtualKnowledgeTree() when workspace available
  addWorkspaceChangeSubscription(() => {
    tryInitializeVirtualKnowledgeTree();
  });
}

export class virtualKnowledgeFileSystemProvider implements vscode.FileSystemProvider {
  private ws: CopilotStudioWorkspace;
  private metadata: WsComponentMetadata[] = [];
  private components = new Map<string, { id: string; mtime: number; filename: string; schema: string; agentId: string; agentSchemaName: string}>();
  private _onDidChangeFile = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  readonly onDidChangeFile = this._onDidChangeFile.event;

  constructor(ws: CopilotStudioWorkspace) {
    this.ws = ws;
  }

  // No-op watch implementation and set _onDidChangeFile.fire() to prevent vs code polling.
  // vs code thinks that we have our own watch and will not poll every 5 seconds.
  watch(uri: vscode.Uri, options: { recursive: boolean; excludes: string[] }): vscode.Disposable {
      return { dispose: () => {} };
  }

  async refresh(): Promise<void> {
    const { syncInfo, workspaceUri } = this.ws;
    if (!syncInfo || !syncInfo.dataverseEndpoint) {
      return;
    }

    const trackPath = getTrackPath(workspaceUri);
    const track = await loadChangeTrack(trackPath);
    const botHandler = await getDataverseBotHandler(syncInfo);
    this.metadata = await botHandler.listWsComponentMetadata(syncInfo);
    const remoteFilenames = new Set<string>();

    this.components.clear();
    for (const componentMetadata of this.metadata) {
      if ((componentMetadata.schemaName.includes('.file.') || componentMetadata.schemaName.includes('.knowledge.')) &&
          !componentMetadata.schemaName.includes('.files.') &&
          componentMetadata.filename) {
        const filename = decodeURIComponent(componentMetadata.filename);
        const displayName = `${filename} (${componentMetadata.agentSchemaName})`;
        this.components.set(displayName, {
          id: componentMetadata.id,
          mtime: componentMetadata.modifiedOn,
          filename,
          schema: componentMetadata.schemaName,
          agentId: componentMetadata.agentId,
          agentSchemaName: componentMetadata.agentSchemaName
        });
        remoteFilenames.add(filename);
      }
    }

    const deletedLocallyDueToRemote: string[] = [];

    for (const file in track) {
      const localPath = path.join(getFilesDir(workspaceUri, track[file].agentSchemaName), file);
      const existsLocally = await fs.stat(localPath).then(() => true).catch(() => false);
      const existsRemotely = remoteFilenames.has(file);

      if (!existsRemotely) {
        if (existsLocally) {
          try {
            await fs.unlink(localPath);
            deletedLocallyDueToRemote.push(file);
          } catch {}
        }

        if (track[file].localChangeType !== ChangeType.Delete) {
          track[file].remoteChangeType = ChangeType.Delete;
        }
      } else {
        if (track[file].remoteChangeType === ChangeType.Delete) {
          delete track[file].remoteChangeType;
        }
        if (track[file].localChangeType === ChangeType.Delete) {
          delete track[file].localChangeType;
        }
      }
    }
    
    if (deletedLocallyDueToRemote.length > 0) {
      logger.logInfo(TelemetryEventsKeys.VirtualKnowledgeFileProgress, `Files deleted locally due to remote changes: <pii>${deletedLocallyDueToRemote.join(', ')}</pii>`);
    }

    for (const file of Object.keys(track)) {
      const localPath = path.join(getFilesDir(workspaceUri, track[file].agentSchemaName), file);
      const existsLocally = await fs.stat(localPath).then(() => true).catch(() => false);
      if (!existsLocally) {
        delete track[file];
      }
    }

    await saveChangeTrack(trackPath, track);

    this._onDidChangeFile.fire([
      {
        type: vscode.FileChangeType.Changed,
        uri: vscode.Uri.parse('virtualKnowledge:/'),
      },
    ]);
  }

  async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
    if (uri.path === '/') {
      return { type: vscode.FileType.Directory, ctime: 0, mtime: 0, size: 0 };
    }

    const key = uri.path.slice(1);
    const component = this.components.get(key);
    if (!component) {
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `File not found in components map: <pii>${key}</pii>`);
      throw vscode.FileSystemError.FileNotFound();
    }
    return { type: vscode.FileType.File, ctime: 0, mtime: component.mtime, size: 0 };
  }

  async readDirectory(uri: vscode.Uri): Promise<[string, vscode.FileType][]> {
    if (uri.path !== '/') {
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `Invalid directory access: <pii>${uri.path}</pii>`);
      throw vscode.FileSystemError.FileNotFound();
    }
    return Array.from(this.components.keys()).map(name => [name, vscode.FileType.File]);
  }

  async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const { syncInfo, workspaceUri } = this.ws;
    if (!syncInfo || !syncInfo.dataverseEndpoint) {
      return new Uint8Array();
    }

    const key = decodeURIComponent(uri.path.slice(1));
    const component = this.components.get(key);
    if (!component) {
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `File not found: <pii>${key}</pii>`);
      throw vscode.FileSystemError.FileNotFound();
    }

    const filename = component.filename;
    const trackPath = getTrackPath(workspaceUri);
    const localPath = path.join(getFilesDir(workspaceUri, component.agentSchemaName), filename);
    const track: ChangeTrack = await loadChangeTrack(trackPath) || {};
    const existsLocally = await fs.stat(localPath).then(() => true).catch(() => false);
    const isText = await isTextFile(filename);

    let remoteContent: Buffer;
    try {
      const botHandler = await getDataverseBotHandler(syncInfo);
      remoteContent = await botHandler.downloadKnowledgeFile(component.id);
    } catch (err) {
      logger.logError(TelemetryEventsKeys.VirtualKnowledgeFileError, `Failed to download <pii>${filename}</pii>: ${err}`);
      throw vscode.FileSystemError.Unavailable();
    }

    let shouldSave = false;
    
    track[filename] = {
      ...(track[filename] ?? {}),
      remoteModifiedOn: component.mtime,
      size: remoteContent.length,
      schema: component.schema,
      agentId: component.agentId,
      agentSchemaName: component.agentSchemaName
    };

    if (!existsLocally) {
      shouldSave = true;
      track[filename].remoteChangeType = ChangeType.Create;
    } else if (isText) {
      const localContent = await fs.readFile(localPath, 'utf8');
      const remoteText = remoteContent.toString('utf8');
      if (localContent !== remoteText) {
        const resolution = await resolveConflict(filename, localPath, remoteText, isText);
        if (resolution === ConflictResolution.UseLocal) {
          track[filename].localChangeType = ChangeType.Update;
          await saveChangeTrack(trackPath, track);
          const doc = await vscode.workspace.openTextDocument(localPath);
          await vscode.window.showTextDocument(doc, { preview: false });
          return await fs.readFile(localPath);
        } else if (resolution === ConflictResolution.UseRemote) {
          shouldSave = true;
        } else {          
          return await fs.readFile(localPath);
        }
      }
    } else {
      shouldSave = true;
    }

    // After saving remote content locally:
    if (shouldSave) {
      await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: `Saving ${filename} to local...`,
        cancellable: false
      }, async () => {
        const tempPath = path.join(os.tmpdir(), `mcs-save-${filename}-${Date.now()}-${randId(3)}`);
        await safeSaveFile(localPath, tempPath, remoteContent);        
        delete track[filename].remoteChangeType;
        delete track[filename].localChangeType;

        logger.logInfo(TelemetryEventsKeys.VirtualKnowledgeFileProgress, `File downloaded and saved to <pii>knowledge\\files\\${filename}</pii>`);
      });
    }

    const stat = await fs.stat(localPath);
    track[filename].localModifiedOn = stat.mtimeMs;

    await saveChangeTrack(trackPath, track);    

    if (isText) {
      const doc = await vscode.workspace.openTextDocument(localPath);
      await vscode.window.showTextDocument(doc, { preview: false });
    }

    return remoteContent;
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
