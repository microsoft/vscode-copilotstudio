import * as vscode from 'vscode';
import logger from '../services/logger';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { CopilotStudioWorkspace, getWorkspaceByUri } from '../sync/localWorkspaces';
import { AgentConnectionView } from '../types';
import {
  applyConnectionBindings,
  createConnectionReference,
  declareConnectionReferences,
  listAgentConnections
} from './connectionCatalog';
import { awaitConnectionCreation, resolveCreatedConnectionId } from './connectionCreation';
import { isCustomConnectorInternalId, waitForCustomConnectorReady } from './connectorReadiness';
import { pickConnector } from './connectorPicker';

export interface AddConnectionReferenceArgs {
  documentUri: vscode.Uri;
  range: vscode.Range;
  currentValue: string;
}

const parseConnectorInternalId = (value: string): string | undefined => {
  if (!value) {
    return undefined;
  }
  for (const segment of value.split('.')) {
    if (segment.toLowerCase().startsWith('shared_')) {
      return segment;
    }
  }
  return undefined;
};

const findView = (views: AgentConnectionView[], logicalName: string): AgentConnectionView | undefined =>
  views.find(v => v.connectionReferenceLogicalName.toLowerCase() === logicalName.toLowerCase());

const writeReferenceIntoDocument = async (documentUri: vscode.Uri, range: vscode.Range, logicalName: string): Promise<void> => {
  const edit = new vscode.WorkspaceEdit();
  edit.replace(documentUri, range, logicalName);
  await vscode.workspace.applyEdit(edit);
  const document = await vscode.workspace.openTextDocument(documentUri);
  await document.save();
};

const bindExistingConnection = async (workspace: CopilotStudioWorkspace, view: AgentConnectionView, logicalName: string): Promise<boolean> => {
  const connected = (view.candidates ?? []).filter(c => (c.status || '').toLowerCase() === 'connected');
  if (connected.length === 0) {
    void vscode.window.showInformationMessage('No connected connections are available for this connector. Create a new connection instead.');
    return false;
  }

  const pick = await vscode.window.showQuickPick(
    connected.map(c => ({
      label: c.displayName || c.name,
      description: c.owner || undefined,
      detail: c.name,
      connectionId: c.name,
      connectionDisplayName: c.displayName
    })),
    { title: 'Select a connection to bind', placeHolder: 'Choose an existing connection' }
  );
  if (!pick) {
    return false;
  }

  await applyConnectionBindings(workspace, [
    {
      connectionReferenceLogicalName: logicalName,
      connectionId: pick.connectionId,
      connectionDisplayName: pick.connectionDisplayName || undefined
    }
  ]);
  return true;
};

const createAndBindConnection = async (workspace: CopilotStudioWorkspace, connectorInternalId: string, logicalName: string): Promise<boolean> => {
  const syncInfo = workspace.syncInfo;
  if (!syncInfo) {
    return false;
  }

  const tokenSource = new vscode.CancellationTokenSource();
  try {
    if (isCustomConnectorInternalId(connectorInternalId)) {
      await waitForCustomConnectorReady(workspace, connectorInternalId, { cancellationToken: tokenSource.token });
    }

    const result = await awaitConnectionCreation({
      connectorName: connectorInternalId,
      environmentId: syncInfo.environmentId,
      clusterCategory: syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory,
      cancellationToken: tokenSource.token
    });

    if (result.status === 'cancelled') {
      return false;
    }
    if (result.status === 'error') {
      void vscode.window.showErrorMessage(result.errorMessage ?? 'Connection creation failed.');
      return false;
    }

    const connectionId = resolveCreatedConnectionId(result);
    if (!connectionId) {
      void vscode.window.showErrorMessage('Connection was created but no identifier was returned.');
      return false;
    }

    await applyConnectionBindings(workspace, [
      {
        connectionReferenceLogicalName: logicalName,
        connectionId,
        connectionDisplayName: result.displayName || connectorInternalId
      }
    ]);
    return true;
  } finally {
    tokenSource.dispose();
  }
};

export const registerAddConnectionReferenceCommand = (context: vscode.ExtensionContext): void => {
  const command = vscode.commands.registerCommand(
    'microsoft-copilot-studio.addConnectionReferenceForDiagnostic',
    async (args?: AddConnectionReferenceArgs) => {
      if (!args) {
        return;
      }

      const workspace = getWorkspaceByUri(args.documentUri);
      if (!workspace?.syncInfo) {
        void vscode.window.showWarningMessage('Connect this agent to an environment before adding connection references.');
        return;
      }

      try {
        let connectorInternalId = parseConnectorInternalId(args.currentValue);
        if (!connectorInternalId) {
          connectorInternalId = await pickConnector(workspace);
        }
        if (!connectorInternalId) {
          return;
        }

        let logicalName = args.currentValue;
        let needsWriteBack = false;

        const declareResult = await declareConnectionReferences(workspace, [args.currentValue]);
        if (declareResult.invalid.length > 0) {
          const created = await createConnectionReference(workspace, connectorInternalId);
          logicalName = created.logicalName;
          needsWriteBack = true;
        }

        const choice = await vscode.window.showQuickPick(
          [
            { label: 'Use an existing connection', action: 'existing' as const },
            { label: 'Create a new connection', action: 'create' as const }
          ],
          { title: 'Add connection reference', placeHolder: 'Bind the connection reference to a connection' }
        );
        if (!choice) {
          return;
        }

        let bound = false;
        if (choice.action === 'existing') {
          const views = await listAgentConnections(workspace);
          const view = findView(views, logicalName);
          if (!view) {
            void vscode.window.showErrorMessage('The connection reference could not be found after declaring it.');
            return;
          }
          bound = await bindExistingConnection(workspace, view, logicalName);
        } else {
          bound = await createAndBindConnection(workspace, connectorInternalId, logicalName);
        }

        if (!bound) {
          return;
        }

        if (needsWriteBack) {
          await writeReferenceIntoDocument(args.documentUri, args.range, logicalName);
        }

        void vscode.window.showInformationMessage(`Connection reference '${logicalName}' is ready.`);
      } catch (error) {
        const message = (error as Error).message ?? 'Failed to add the connection reference.';
        logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Failed to add connection reference: <pii>${message}</pii>`);
        void vscode.window.showErrorMessage(message);
      }
    }
  );

  context.subscriptions.push(command);
};
