import * as vscode from 'vscode';
import { lspClient } from '../services/lspClient';
import { LspMethods } from '../constants';

const agentDirectories: Set<string> = new Set();

export function initializeAgentDirectoryHandler(context: vscode.ExtensionContext) {
  // file decoration provider for agent directories
  const decoEmitter = new vscode.EventEmitter<vscode.Uri | vscode.Uri[] | undefined>();
  const agentDecorator: vscode.FileDecorationProvider = {
    onDidChangeFileDecorations: decoEmitter.event,
    provideFileDecoration(uri) {
      if (agentDirectories.has(uri.toString())) {
        // badges with value of more than 2 characters will be DISCARDED by VS Code
        return {
          badge: '{}',
          tooltip: 'Agent Directory',
          color: new vscode.ThemeColor('copilotStudio.agentDirectoryBackground')
        };
      }
    }
  };
  context.subscriptions.push(vscode.window.registerFileDecorationProvider(agentDecorator));

  // listen for agent directory changes from the server
  var initAgentHandler = lspClient.onNotification(LspMethods.AGENT_DIRECTORY_CHANGE,
    (p: { uris: string[] }) => {
      // update local agent directories
      const before = new Set(agentDirectories);
      agentDirectories.clear();
      for (const raw of p.uris ?? []) {
        const parsed = vscode.Uri.parse(raw.replace(/\/$/, ''));
        agentDirectories.add(parsed.toString());
      }

      const diffDirectories = computeDiffDirectories(before, agentDirectories);

      // tell VS Code to ask provideFileDecoration again
      decoEmitter.fire(diffDirectories);
    });
  context.subscriptions.push(initAgentHandler);
}

/**
 * Returns one array that contains every directory that was added **or**
 * removed when going from `before` to `after`.
 */
function computeDiffDirectories(
  before: ReadonlySet<string>,
  after: ReadonlySet<string>
): vscode.Uri[] {
  const changed: vscode.Uri[] = [];

  // added
  for (const uri of after) {
    if (!before.has(uri)) {
      changed.push(vscode.Uri.parse(uri));
    }
  }

  // removed
  for (const uri of before) {
    if (!after.has(uri)) {
      changed.push(vscode.Uri.parse(uri));
    }
  }

  return changed;
}