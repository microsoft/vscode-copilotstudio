import * as vscode from 'vscode';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';
import { resolveStringValueSpan } from './workflowParser';

export const FOCUS_NODE_COMMAND = 'microsoft-copilot-studio.workflow.focusNode';
export const EDIT_EMBEDDED_JSON_COMMAND = 'microsoft-copilot-studio.workflow.editEmbeddedJson';
const EMBEDDED_SCHEME = 'mcs-wf-embedded';

export interface FocusNodeArgs {
  uri: string;
  offset: number;
  length: number;
}

export interface EditEmbeddedJsonArgs {
  uri: string;
  path: (string | number)[];
  label: string;
}

async function focusNode(args: FocusNodeArgs): Promise<void> {
  try {
    const uri = vscode.Uri.parse(args.uri);
    const doc = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(doc, { preview: false });

    const startPos = doc.positionAt(args.offset);
    const endPos = doc.positionAt(args.offset + Math.max(args.length, 1));
    editor.selection = new vscode.Selection(startPos, startPos);

    await vscode.commands.executeCommand('editor.foldAll');
    await vscode.commands.executeCommand('editor.unfoldRecursively');
    editor.revealRange(new vscode.Range(startPos, endPos), vscode.TextEditorRevealType.AtTop);
  } catch (error) {
    logger.logError(
      TelemetryEventsKeys.WorkflowFocusNodeError,
      `Failed to focus workflow node: <pii>${error instanceof Error ? error.message : String(error)}</pii>`,
    );
  }
}

function buildEmbeddedUri(sourceUri: vscode.Uri, path: (string | number)[], label: string): vscode.Uri {
  const query = encodeURIComponent(JSON.stringify({ src: sourceUri.toString(), path }));
  const safeLabel = label.replace(/[^A-Za-z0-9._-]/g, '_');
  return vscode.Uri.from({
    scheme: EMBEDDED_SCHEME,
    path: `/${safeLabel}.json`,
    query,
  });
}

interface EmbeddedTarget {
  src: vscode.Uri;
  path: (string | number)[];
}

function decodeEmbeddedUri(uri: vscode.Uri): EmbeddedTarget | undefined {
  try {
    const parsed = JSON.parse(decodeURIComponent(uri.query)) as { src: string; path: (string | number)[] };
    return { src: vscode.Uri.parse(parsed.src), path: parsed.path };
  } catch {
    return undefined;
  }
}

class EmbeddedJsonFileSystemProvider implements vscode.FileSystemProvider {
  private readonly _emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
  public readonly onDidChangeFile = this._emitter.event;

  public watch(): vscode.Disposable {
    return new vscode.Disposable(() => undefined);
  }

  public async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
    const content = await this.readFile(uri);
    return {
      type: vscode.FileType.File,
      ctime: Date.now(),
      mtime: Date.now(),
      size: content.byteLength,
    };
  }

  public readDirectory(): [string, vscode.FileType][] {
    return [];
  }

  public createDirectory(): void {
  }

  public async readFile(uri: vscode.Uri): Promise<Uint8Array> {
    const target = decodeEmbeddedUri(uri);
    if (!target) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }
    const sourceDoc = await vscode.workspace.openTextDocument(target.src);
    const valueSpan = resolveStringValueSpan(sourceDoc.getText(), target.path);
    if (!valueSpan) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }
    const rawLiteral = sourceDoc.getText(
      new vscode.Range(
        sourceDoc.positionAt(valueSpan.offset),
        sourceDoc.positionAt(valueSpan.offset + valueSpan.length),
      ),
    );
    let inner: unknown;
    try {
      const stringValue = JSON.parse(rawLiteral) as string;
      inner = JSON.parse(stringValue);
    } catch {
      throw vscode.FileSystemError.FileNotFound(uri);
    }
    const pretty = JSON.stringify(inner, null, 2);
    return Buffer.from(pretty, 'utf8');
  }

  public async writeFile(uri: vscode.Uri, content: Uint8Array): Promise<void> {
    const target = decodeEmbeddedUri(uri);
    if (!target) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }
    const text = Buffer.from(content).toString('utf8');

    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch (error) {
      void vscode.window.showErrorMessage(`Invalid JSON — not saved back to workflow: ${error}`);
      throw vscode.FileSystemError.NoPermissions(uri);
    }

    const sourceDoc = await vscode.workspace.openTextDocument(target.src);
    const valueSpan = resolveStringValueSpan(sourceDoc.getText(), target.path);
    if (!valueSpan) {
      throw vscode.FileSystemError.FileNotFound(uri);
    }

    const minified = JSON.stringify(parsed);
    const escapedLiteral = JSON.stringify(minified);

    const range = new vscode.Range(
      sourceDoc.positionAt(valueSpan.offset),
      sourceDoc.positionAt(valueSpan.offset + valueSpan.length),
    );
    const edit = new vscode.WorkspaceEdit();
    edit.replace(target.src, range, escapedLiteral);
    const applied = await vscode.workspace.applyEdit(edit);
    if (!applied) {
      throw vscode.FileSystemError.NoPermissions(uri);
    }

    this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
  }

  public delete(): void {
  }

  public rename(): void {
  }
}

async function editEmbeddedJson(args: EditEmbeddedJsonArgs): Promise<void> {
  try {
    const sourceUri = vscode.Uri.parse(args.uri);
    const embeddedUri = buildEmbeddedUri(sourceUri, args.path, args.label);
    const doc = await vscode.workspace.openTextDocument(embeddedUri);
    await vscode.languages.setTextDocumentLanguage(doc, 'json');
    await vscode.window.showTextDocument(doc, { preview: false });
  } catch (error) {
    logger.logError(
      TelemetryEventsKeys.WorkflowEditEmbeddedJsonError,
      `Failed to open embedded JSON editor: <pii>${error instanceof Error ? error.message : String(error)}</pii>`,
    );
    void vscode.window.showErrorMessage(`Could not open embedded JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
}

export function registerWorkflowCommands(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.workspace.registerFileSystemProvider(EMBEDDED_SCHEME, new EmbeddedJsonFileSystemProvider(), {
      isCaseSensitive: true,
    }),
    vscode.commands.registerCommand(FOCUS_NODE_COMMAND, (args: FocusNodeArgs) => focusNode(args)),
    vscode.commands.registerCommand(EDIT_EMBEDDED_JSON_COMMAND, (args: EditEmbeddedJsonArgs) =>
      editEmbeddedJson(args),
    ),
  );
}
