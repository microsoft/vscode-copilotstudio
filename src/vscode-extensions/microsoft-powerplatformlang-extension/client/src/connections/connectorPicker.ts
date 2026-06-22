import * as vscode from 'vscode';
import { CopilotStudioWorkspace } from '../sync/localWorkspaces';
import { ConnectorInfo } from '../types';
import { listConnectors } from './connectionCatalog';

interface ConnectorPickItem extends vscode.QuickPickItem {
  connector: ConnectorInfo;
}

export const pickConnector = async (
  workspace: CopilotStudioWorkspace,
  usedConnectorIds: ReadonlySet<string> = new Set()
): Promise<string | undefined> => {
  const quickPick = vscode.window.createQuickPick<ConnectorPickItem>();
  quickPick.title = 'Select a connector';
  quickPick.placeholder = 'Loading connectors…';
  quickPick.busy = true;
  quickPick.matchOnDescription = true;
  quickPick.matchOnDetail = true;

  let hidden = false;
  let resolveResult: ((value: string | undefined) => void) | undefined;
  quickPick.onDidHide(() => {
    hidden = true;
    resolveResult?.(undefined);
  });
  quickPick.show();

  try {
    let connectors: ConnectorInfo[];
    try {
      connectors = await listConnectors(workspace);
    } catch (error) {
      quickPick.hide();
      void vscode.window.showErrorMessage((error as Error).message ?? 'Failed to list connectors.');
      return undefined;
    }

    if (hidden) {
      return undefined;
    }

    if (connectors.length === 0) {
      quickPick.hide();
      void vscode.window.showInformationMessage('No connectors were found in this environment.');
      return undefined;
    }

    const sorted = [...connectors].sort((a, b) => {
      const aUsed = usedConnectorIds.has(a.internalId) ? 0 : 1;
      const bUsed = usedConnectorIds.has(b.internalId) ? 0 : 1;
      if (aUsed !== bUsed) {
        return aUsed - bUsed;
      }
      return a.displayName.localeCompare(b.displayName);
    });

    quickPick.items = sorted.map(connector => {
      const tier = connector.tier ? ` · ${connector.tier}` : '';
      const used = usedConnectorIds.has(connector.internalId) ? ' · in use' : '';
      return {
        label: connector.displayName,
        description: `${connector.publisher || ''}${tier}${used}`.trim(),
        detail: connector.internalId,
        connector
      };
    });
    quickPick.placeholder = 'Search connectors by name';
    quickPick.busy = false;

    return await new Promise<string | undefined>(resolve => {
      resolveResult = resolve;
      quickPick.onDidAccept(() => {
        const picked = quickPick.selectedItems[0];
        resolve(picked?.connector.internalId);
        quickPick.hide();
      });
    });
  } finally {
    quickPick.dispose();
  }
};
