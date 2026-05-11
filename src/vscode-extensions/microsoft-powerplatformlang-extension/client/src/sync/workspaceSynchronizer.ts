import * as vscode from 'vscode';
import { resetAccount } from '../clients/account';
import { SyncRequest, SyncResponse, WorkflowResponse } from '../types';
import { CopilotStudioWorkspace, tryRepairAgentManagementEndpoint } from './localWorkspaces';
import { uploadKnowledgeFiles } from '../knowledgeFiles/uploadKnowledgeFiles';
import { virtualKnowledgeFileSystemProvider } from '../knowledgeFiles/virtualKnowledgeFile';
import { knowledgeTreeDataProvider } from '../knowledgeFiles/knowledgeFileTree';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';

let treeDataProvider: knowledgeTreeDataProvider | undefined;

const map = new Map<string, WorkspaceSynchronizer>();

export enum SyncState {
  Idle,
  Fetching,
  Pulling,
  Pushing,
}

/** Global event fired whenever sync activity changes. Used to refresh UI. */
const _onAnySyncStateChanged = new vscode.EventEmitter<void>();
export const onAnySyncStateChanged = _onAnySyncStateChanged.event;

/** workspaceUri of the workspace currently inside `withSyncCommandBusy`. */
let _activeSyncUri: string | undefined;

/** Returns the workspaceUri of the workspace currently being synced, or undefined. */
export function getActiveSyncUri(): string | undefined {
  return _activeSyncUri;
}

/** Returns the synchronizer's current SyncState for `workspaceUri`, or Idle if none. */
export function getSyncStateFor(workspaceUri: string): SyncState {
  return map.get(workspaceUri)?.syncState ?? SyncState.Idle;
}

/**
 * Wrap a sync command's body so the busy state is held for the full duration
 * across multiple fetch/pull/push steps. Surfaces a native progress bar at
 * the top of the Agent Changes tree view and disables sync buttons via the
 * `mcs.isSyncing` context key.
 */
export async function withSyncCommandBusy<T>(workspaceUri: string, body: () => Promise<T>): Promise<T> {
  _activeSyncUri = workspaceUri;
  _onAnySyncStateChanged.fire();
  try {
    return await vscode.window.withProgress(
      { location: { viewId: 'agent-changes' } },
      body
    );
  } finally {
    _activeSyncUri = undefined;
    _onAnySyncStateChanged.fire();
  }
}

export interface WorkspaceSynchronizer {
    workspace: CopilotStudioWorkspace;
    syncState: SyncState;
    push: () => Promise<SyncResponse | undefined>;
    pull: (virtualProvider: virtualKnowledgeFileSystemProvider) => Promise<SyncResponse | undefined >;
    fetch: () => Promise<void>;
    subscribe: (listener: SyncStateListener) => () => void;
}

interface SyncStateListener {
  (state: SyncState): void;
}

export function getOrAddSynchronizer(ws: CopilotStudioWorkspace): WorkspaceSynchronizer {
  const uri = ws.workspaceUri.toString();
  if (map.has(uri)) {
    return map.get(uri)!;
  }

  const synchronizer = getSynchronizer(ws);
  map.set(uri, synchronizer);
  return synchronizer;
}

function getSynchronizer(ws: CopilotStudioWorkspace): WorkspaceSynchronizer {
  let currentState = SyncState.Idle;
  const listeners: SyncStateListener[] = [];

  function updateSyncState(newState: SyncState) {
    currentState = newState;
    listeners.forEach(listener => listener(newState));
    _onAnySyncStateChanged.fire();
  }

  async function executeSyncOperation<T>(operation: () => Promise<T>, newState: SyncState): Promise<T> {
    // Prevent concurrent operations
    if (currentState !== SyncState.Idle) {
      throw new Error('Another sync operation is in progress');
    }

    try {
      updateSyncState(newState);
      const result = await operation();
      return result;
    } finally {
      updateSyncState(SyncState.Idle);
    }
  }

  return {
    workspace: ws,
    get syncState() { return currentState; },
    push: async (): Promise<SyncResponse> => {
      return await executeSyncOperation(async () => {
        await uploadKnowledgeFiles(ws);
        return await sync(ws, 'applying changes', LspMethods.SYNC_PUSH, false);
      }, SyncState.Pushing);
    },
    pull: async (virtualProvider: virtualKnowledgeFileSystemProvider): Promise<SyncResponse> => {
      return await executeSyncOperation(async () => {
        // Get virtual knowledge files
        if (virtualProvider) {
          await virtualProvider.refresh();
          if (!treeDataProvider) {
            treeDataProvider = new knowledgeTreeDataProvider(virtualProvider);
            vscode.window.registerTreeDataProvider('virtual-knowledge-files', treeDataProvider);
          }
          treeDataProvider.refresh();
        }
        return await sync(ws, "getting changes", LspMethods.SYNC_PULL, false);
      }, SyncState.Pulling);
    },
    fetch: async () => {
      await executeSyncOperation(
        async () => {
          await sync(ws, "previewing changes", LspMethods.GET_REMOTE_CHANGES, true);
        },
        SyncState.Fetching
      );
    },
    subscribe: (listener: SyncStateListener): () => void => {
      listeners.push(listener);
      return () => {
        const index = listeners.indexOf(listener);
        if (index !== -1) {
          listeners.splice(index, 1);
        }
      };
    }
  };
}

export async function sync(workspace: CopilotStudioWorkspace, displayText: string, methodName: string, silent: boolean): Promise<SyncResponse> {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    throw new Error(`${displayText} failed. Connection file .mcs::conn.json is missing, please clone again.`);
  }

  // On-demand repair: resolve missing agentManagementEndpoint from BAP single-environment lookup.
  // PAC-cloned workspaces may have null endpoint when user lacks PP admin role.
  if (!syncInfo.agentManagementEndpoint) {
    await tryRepairAgentManagementEndpoint(syncInfo, workspaceUri);
  }

  const { accountInfo, agentManagementEndpoint, dataverseEndpoint, environmentId } = syncInfo;
  if (!dataverseEndpoint || !environmentId || !agentManagementEndpoint) {
    throw new Error(`${displayText} failed. Connection settings in .mcs::conn.json are incomplete or invalid, please clone again.`);
  }

  const request: SyncRequest = {
    ...await buildLspRequestPayload(syncInfo),
    workspaceUri,
  };

  try {
    const result = silent
      ? await lspClient.sendRequest<SyncResponse>(methodName, request)
      : await vscode.window.withProgress({ location: vscode.ProgressLocation.SourceControl }, async () => {
        return await lspClient.sendRequest<SyncResponse>(methodName, request);
      });
    logger.logInfo(TelemetryEventsKeys.SyncWorkspaceSuccess, `Successfully completed ${displayText}`);
    logWorkflowIssues(result.workflowResponse);
    logNewCustomConnectors(result.newlyCreatedCustomConnectors, workspace);
    return result;
  } catch (error) {
    if ((error as Error).message?.includes("UserNotMemberOfOrg")) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Your current account does not have permission. Please sign in with the account <pii>(${accountInfo.accountEmail ?? accountInfo.accountId})</pii> to perform this operation.`);
      try {
        resetAccount();
        return await sync(workspace, displayText, methodName, silent); // Retry sync with new log in
      } catch (error) {
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Re-authentication failed: ${(error as Error).message}`);
        throw error;
      }
    } else {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Error ${displayText}: ${(error as Error).message}`);
      throw error;
    }
  }
}

export function logWorkflowIssues(workflows: WorkflowResponse[] | undefined) {
  if (!workflows?.length) {
    return;
  }

  const disabledWorkflows: string[] = [];
  const failedWorkflows: string[] = [];

  for (const w of workflows) {
    if (w.isDisabled) {
      disabledWorkflows.push(w.workflowName);
    }
    else if (w.errorMessage) {
      failedWorkflows.push(`${w.workflowName}: ${w.errorMessage}`);
    }
  }

  if (disabledWorkflows.length > 0) {
    logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, `These workflows need reestablish connection and need to be enabled in MCS portal: ${disabledWorkflows.join(", ")}`);
  } else if (failedWorkflows.length > 0) {
    logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Workflow errors: ${failedWorkflows.join(", ")}`);
  }
}

export function logNewCustomConnectors(connectors: string[] | undefined, workspace: CopilotStudioWorkspace) {
  logNewCustomConnectorsRaw(connectors, workspace.workspaceUri);
}

export function logNewCustomConnectorsRaw(connectors: string[] | undefined, workspaceUri: string) {
  if (!connectors?.length) {
    return;
  }
  const agentName = workspaceUri.split(/[\\/]/).filter(Boolean).pop() ?? 'agent';
  for (const connectorName of connectors) {
    logger.logWarning(
      TelemetryEventsKeys.SyncWorkspaceSuccess,
      `New custom connector '${connectorName}' was created. ` +
      `Go to Power Apps maker (https://make.powerapps.com) to create a Connection for this connector, ` +
      `then update the connection reference in VS Code and apply the change.`
    );
  }
}